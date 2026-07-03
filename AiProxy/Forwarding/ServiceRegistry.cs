using AiProxy.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace AiProxy.Forwarding;

/// <summary>
/// URL 前缀 → 下游 AI 服务的解析器。
/// 基于 HttpContext.Request.Path 首段解析当前请求所属的 AiServiceOptions，剥离前缀后返回剩余路径供转发。
/// 通过 IOptionsMonitor&lt;AppOptions&gt; 读取（Singleton 安全），配置文件变更后对新请求即时生效。
/// 新增/删除服务（经管理面板写回 appsettings.json）无需重启即可命中。
/// </summary>
public sealed class ServiceRegistry
{
    private readonly IOptionsMonitor<AppOptions> _options;

    public ServiceRegistry(IOptionsMonitor<AppOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// 按请求路径首段匹配下游服务。
    /// 路径 /&lt;prefix&gt;/v1/... 匹配 PathPrefix=prefix 的服务，返回剥离首段后的剩余路径 /v1/...
    /// 匹配大小写不敏感（与 ASP.NET 路由约定一致）；未匹配返回 null + 原路径。
    /// </summary>
    public (AiServiceOptions? service, PathString remainingPath) FindByPath(PathString path)
    {
        var services = _options.CurrentValue.AiServices;
        foreach (var s in services)
        {
            if (string.IsNullOrEmpty(s.PathPrefix))
            {
                continue;
            }
            var prefixPath = new PathString("/" + s.PathPrefix);
            if (path.StartsWithSegments(prefixPath, StringComparison.OrdinalIgnoreCase, out var remaining))
            {
                return (s, remaining);
            }
        }
        return (null, path);
    }

    /// <summary>按前缀字符串查找下游服务配置（重放链路用），找不到返回 null</summary>
    public AiServiceOptions? FindByPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return null;
        }
        foreach (var s in _options.CurrentValue.AiServices)
        {
            if (string.Equals(s.PathPrefix, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>按服务名称查找下游服务配置（重放链路用），找不到返回 null</summary>
    public AiServiceOptions? FindByName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        foreach (var s in _options.CurrentValue.AiServices)
        {
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>当前已配置的可用前缀列表（供未匹配路由 400 响应展示）</summary>
    public IReadOnlyList<string> AvailablePrefixes =>
        _options.CurrentValue.AiServices
            .Where(s => !string.IsNullOrEmpty(s.PathPrefix))
            .Select(s => s.PathPrefix)
            .ToList();

    /// <summary>按目标主机名查找下游服务配置（SSL 证书验证用），找不到返回 null</summary>
    public AiServiceOptions? GetServiceByHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }
        foreach (var s in _options.CurrentValue.AiServices)
        {
            if (Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>
    /// 从路径中剥离指定前缀首段，返回剩余路径（重放链路复用）。
    /// 如 path=/openai/chat/completions、prefix=openai → /chat/completions。
    /// 未匹配前缀则原样返回。
    /// </summary>
    public static PathString StripPrefix(PathString path, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return path;
        }
        var prefixPath = new PathString("/" + prefix.TrimStart('/'));
        if (path.StartsWithSegments(prefixPath, StringComparison.OrdinalIgnoreCase, out var remaining))
        {
            return remaining;
        }
        return path;
    }
}
