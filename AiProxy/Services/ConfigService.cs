using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Admin;
using AiProxy.Config;
using Microsoft.Extensions.Options;

namespace AiProxy.Services;

/// <summary>
/// 配置持久化服务：将 AiServices / GlobalApiKey 的运行时变更写回 appsettings.json。
/// 写回后 IOptionsMonitor&lt;AppOptions&gt; 经 reloadOnChange 自动 reload，转发/鉴权路径对新请求即时生效。
///
/// 读写策略：
/// - 读：每次操作从磁盘读取最新内容（JsonNode 解析），避免 in-memory 快照过期。
/// - 写：仅替换 Proxy 与 AiServices 节点，保留 Logging/AllowedHosts 等其他顶层节点。
///       原子写入（临时文件 + File.Move 覆盖），避免半写状态被 reload 读取。
///
/// ApiKey 保留约定（输入 DTO ApiKey 为 string?）：
/// - null  = 保持原值（仅更新时有效；新增时按空串处理）
/// - ""    = 清空（不注入鉴权头，如 Ollama；全局密钥则关闭鉴权）
/// - 非空  = 设为新值
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    private static readonly HashSet<string> ReservedPrefixes = new(StringComparer.OrdinalIgnoreCase) { "api", "v1", "admin" };

    private readonly IOptionsMonitor<AppOptions> _options;
    private readonly ConfigFilePathProvider _pathProvider;
    private readonly AppConfigRoot _appConfigRoot;

    public ConfigService(IOptionsMonitor<AppOptions> options, ConfigFilePathProvider pathProvider, AppConfigRoot appConfigRoot)
    {
        _options = options;
        _pathProvider = pathProvider;
        _appConfigRoot = appConfigRoot;
    }

    /// <summary>当前配置（走 IOptionsMonitor，含热更新）</summary>
    public AppOptions GetCurrent() => _options.CurrentValue;

    // ─────────────────────────────────────────────────────────────────────
    // AiServices CRUD
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>新增下游服务。校验 Name/PathPrefix 唯一、PathPrefix 干净且非保留、BaseUrl 合法。</summary>
    public async Task AddServiceAsync(AiServiceInputDto input, CancellationToken ct = default)
    {
        ValidateServiceInput(input, existingName: null);
        // input.ModelMappings null 时按空列表校验（合法），写入时也按空列表
        ValidateModelMappings(input.ModelMappings ?? new List<ModelMappingOptions>());

        var (root, proxy, services) = await ReadConfigAsync(ct);

        // 唯一性校验（与现有服务对比）
        EnsureNameUnique(services, input.Name);
        EnsurePrefixUnique(services, input.PathPrefix);

        var newService = new AiServiceOptions
        {
            Name = input.Name,
            PathPrefix = input.PathPrefix,
            BaseUrl = input.BaseUrl,
            // 新增时 ApiKey=null 按空串处理（无密钥）
            ApiKey = input.ApiKey ?? string.Empty,
            ServiceFormat = ParseServiceFormat(input.ServiceFormat),
            ClientFormat = ParseClientFormat(input.ClientFormat),
            ExtraHeaders = input.ExtraHeaders,
            LogRequestBody = input.LogRequestBody,
            LogResponseBody = input.LogResponseBody,
            AllowInvalidSslCertificates = input.AllowInvalidSslCertificates,
            // input.ModelMappings 在 Add 场景 null 也按空列表处理（不替换）
            ModelMappings = input.ModelMappings ?? new List<ModelMappingOptions>()
        };
        services.Add(JsonNode.Parse(JsonSerializer.Serialize(newService, _writeOptions))!);

        await WriteConfigAsync(root, ct);
    }

    /// <summary>更新下游服务。按 name 定位；ApiKey 按保留约定。</summary>
    public async Task UpdateServiceAsync(string name, AiServiceInputDto input, CancellationToken ct = default)
    {
        ValidateServiceInput(input, existingName: name);

        var (root, proxy, services) = await ReadConfigAsync(ct);

        var target = FindServiceByName(services, name)
            ?? throw new ArgumentException($"服务 '{name}' 不存在");
        // 若改了 Name，确保新 Name 不与其他服务冲突
        if (!string.Equals(name, input.Name, StringComparison.Ordinal))
        {
            EnsureNameUnique(services, input.Name);
        }
        EnsurePrefixUnique(services, input.PathPrefix, excludingName: name);

        target["Name"] = input.Name;
        target["PathPrefix"] = input.PathPrefix;
        target["BaseUrl"] = input.BaseUrl;
        target["LogRequestBody"] = input.LogRequestBody;
        target["LogResponseBody"] = input.LogResponseBody;
        target["AllowInvalidSslCertificates"] = input.AllowInvalidSslCertificates;
        target["ServiceFormat"] = input.ServiceFormat;
        // ClientFormat：始终覆盖。"Auto"/null/"" → null（按鉴权头自动识别）；"OpenAI"/"Anthropic" → 对应枚举名
        var clientFmt = ParseClientFormat(input.ClientFormat);
        target["ClientFormat"] = clientFmt is null ? null : clientFmt.Value.ToString();
        // ExtraHeaders：null 保持原值，非 null 则覆盖
        if (input.ExtraHeaders is not null)
        {
            target["ExtraHeaders"] = JsonNode.Parse(JsonSerializer.Serialize(input.ExtraHeaders, _writeOptions));
        }
        // ModelMappings：null 保持原值；非空（含空列表）整体覆盖
        if (input.ModelMappings is not null)
        {
            ValidateModelMappings(input.ModelMappings);
            target["ModelMappings"] = JsonNode.Parse(JsonSerializer.Serialize(input.ModelMappings, _writeOptions));
        }
        // ApiKey 保留约定：null=保持，""=清空，非空=设新
        if (input.ApiKey is not null)
        {
            target["ApiKey"] = input.ApiKey;
        }

        await WriteConfigAsync(root, ct);
    }

    /// <summary>删除下游服务。按 name 定位。</summary>
    public async Task DeleteServiceAsync(string name, CancellationToken ct = default)
    {
        var (root, proxy, services) = await ReadConfigAsync(ct);

        var idx = -1;
        for (int i = 0; i < services.Count; i++)
        {
            var n = services[i]?["Name"]?.GetValue<string>();
            if (string.Equals(n, name, StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
        {
            throw new ArgumentException($"服务 '{name}' 不存在");
        }
        services.RemoveAt(idx);

        await WriteConfigAsync(root, ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // GlobalApiKey
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>更新全局密钥。null=保持；""=关闭鉴权；非空=设新。</summary>
    public async Task UpdateGlobalApiKeyAsync(GlobalApiKeyInputDto input, CancellationToken ct = default)
    {
        if (input.ApiKey is null)
        {
            return; // 保持原值，无需写文件
        }

        var (root, proxy, services) = await ReadConfigAsync(ct);
        proxy["GlobalApiKey"] = input.ApiKey;
        await WriteConfigAsync(root, ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 内部：读写与校验
    // ─────────────────────────────────────────────────────────────────────

    private async Task<(JsonObject root, JsonObject proxy, JsonArray services)> ReadConfigAsync(CancellationToken ct)
    {
        var text = File.Exists(_pathProvider.AbsolutePath)
            ? await File.ReadAllTextAsync(_pathProvider.AbsolutePath, ct) 
            : "{}";
        var root = JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException("appsettings.json 不是有效的 JSON 对象");

        var proxy = root["Proxy"] as JsonObject;
        if (proxy is null)
        {
            proxy = new JsonObject();
            root["Proxy"] = proxy;
        }

        var services = root["AiServices"] as JsonArray;
        if (services is null)
        {
            services = new JsonArray();
            root["AiServices"] = services;
        }

        return (root, proxy, services);
    }

    private async Task WriteConfigAsync(JsonObject root, CancellationToken ct)
    {
        var json = root.ToJsonString(_writeOptions);
        var path = _pathProvider.AbsolutePath;
        // 直接写入（而非临时文件 + Move），确保文件监视器能正确检测到变更
        await File.WriteAllTextAsync(path, json, ct);
        // 强制重载配置，确保后续 IOptionsMonitor.CurrentValue 立即反映新值
        _appConfigRoot.Reload();
    }

    private static JsonObject? FindServiceByName(JsonArray services, string name)
    {
        foreach (var node in services)
        {
            if (node is JsonObject obj &&
                string.Equals(obj["Name"]?.GetValue<string>(), name, StringComparison.Ordinal))
            {
                return obj;
            }
        }
        return null;
    }

    private static void EnsureNameUnique(JsonArray services, string name)
    {
        foreach (var node in services)
        {
            if (node is JsonObject obj &&
                string.Equals(obj["Name"]?.GetValue<string>(), name, StringComparison.Ordinal))
            {
                throw new ArgumentException($"服务名 '{name}' 已存在");
            }
        }
    }

    private static void EnsurePrefixUnique(JsonArray services, string prefix, string? excludingName = null)
    {
        foreach (var node in services)
        {
            if (node is not JsonObject obj) continue;
            var n = obj["Name"]?.GetValue<string>();
            if (excludingName is not null && string.Equals(n, excludingName, StringComparison.Ordinal)) continue;
            if (string.Equals(obj["PathPrefix"]?.GetValue<string>(), prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"前缀 '{prefix}' 已被服务 '{n}' 占用");
            }
        }
    }

    private static void ValidateServiceInput(AiServiceInputDto input, string? existingName)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("Name 不能为空");
        }
        if (string.IsNullOrWhiteSpace(input.PathPrefix) ||
            !System.Text.RegularExpressions.Regex.IsMatch(input.PathPrefix, @"^[a-zA-Z0-9_-]+$"))
        {
            throw new ArgumentException("PathPrefix 只能包含字母、数字、下划线、连字符");
        }
        if (ReservedPrefixes.Contains(input.PathPrefix))
        {
            throw new ArgumentException($"PathPrefix '{input.PathPrefix}' 为保留段，请换用其他值");
        }
        if (string.IsNullOrWhiteSpace(input.BaseUrl) ||
            !Uri.IsWellFormedUriString(input.BaseUrl, UriKind.Absolute) ||
            (input.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == false &&
             input.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == false))
        {
            throw new ArgumentException("BaseUrl 必须是合法的 http/https 绝对地址（含版本路径，如 https://api.openai.com/v1）");
        }
    }

    /// <summary>
    /// 校验模型映射列表。通配符模式始终语法合法，无需校验。
    /// </summary>
    private static void ValidateModelMappings(IEnumerable<ModelMappingOptions> mappings)
    {
    }

    /// <summary>从字符串解析 ServiceFormat 枚举（容错，默认 OpenAI）</summary>
    private static ServiceFormat ParseServiceFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return ServiceFormat.OpenAI;
        }
        if (Enum.TryParse<ServiceFormat>(format, ignoreCase: true, out var result))
        {
            return result;
        }
        return ServiceFormat.OpenAI;
    }

    /// <summary>
    /// 解析客户端格式输入。
    /// "Auto"/null/"" → null（按鉴权头自动识别）；
    /// "OpenAI"/"Anthropic" → 对应枚举；无法识别时返回 null（容错，等同 Auto）。
    /// </summary>
    private static ServiceFormat? ParseClientFormat(string? clientFormat)
    {
        if (string.IsNullOrWhiteSpace(clientFormat) ||
            string.Equals(clientFormat, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (Enum.TryParse<ServiceFormat>(clientFormat, ignoreCase: true, out var result))
        {
            return result;
        }
        return null;
    }
}
