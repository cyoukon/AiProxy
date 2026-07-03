namespace AiProxy.Admin;

/// <summary>服务概览项（脱敏）</summary>
public sealed class ServiceOverviewDto
{
    public string Name { get; set; } = string.Empty;
    /// <summary>URL 路径前缀（路由键）</summary>
    public string PathPrefix { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>脱敏后的下游密钥</summary>
    public string ApiKey { get; set; } = string.Empty;
    public bool LogRequestBody { get; set; }
    public bool LogResponseBody { get; set; }
    /// <summary>运行状态：always "running"（进程在线即视为运行）</summary>
    public string Status { get; set; } = "running";
    /// <summary>当日调用量（UTC 当天，按 ServiceName 聚合）</summary>
    public long TodayCalls { get; set; }
}

/// <summary>日志列表项（不含 Body 内容，详情接口再返回）</summary>
public sealed class LogListItemDto
{
    public long Id { get; set; }
    public DateTime RequestTime { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string ClientPath { get; set; } = string.Empty;
    public string DownstreamUrl { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? Model { get; set; }
    public bool IsStream { get; set; }
    public bool IsReplay { get; set; }
    public bool IsConverted { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorType { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
}

/// <summary>日志详情（含客户端侧与下游侧完整请求/响应体）</summary>
public sealed class LogDetailDto
{
    public long Id { get; set; }
    public DateTime RequestTime { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? Model { get; set; }
    public bool IsStream { get; set; }
    public bool IsReplay { get; set; }
    public bool IsConverted { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorType { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }

    // 客户端侧
    public string ClientPath { get; set; } = string.Empty;
    public string? ClientFormat { get; set; }
    public string? ClientRequestBody { get; set; }
    public string? ClientResponseBody { get; set; }

    // 下游侧
    public string DownstreamUrl { get; set; } = string.Empty;
    public string? ServiceFormat { get; set; }
    public string? DownstreamRequestBody { get; set; }
    public string? DownstreamResponseBody { get; set; }
}

/// <summary>分页响应包装</summary>
public sealed class PagedResultDto<T>
{
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<T> Items { get; set; } = new();
}

/// <summary>配置查看项（脱敏）</summary>
public sealed class ConfigViewDto
{
    public ProxyConfigViewDto Proxy { get; set; } = new();
    public List<AiServiceConfigViewDto> AiServices { get; set; } = new();
}

public sealed class ProxyConfigViewDto
{
    /// <summary>脱敏后的全局密钥（空则不鉴权）</summary>
    public string GlobalApiKey { get; set; } = string.Empty;
    public string LogDbPath { get; set; } = string.Empty;
    /// <summary>Kestrel 监听地址（启动时固定）</summary>
    public string ListenAddress { get; set; } = string.Empty;
    /// <summary>Kestrel 监听端口（启动时固定，业务与管理共享）</summary>
    public int ListenPort { get; set; }
    public bool AuthEnabled { get; set; }
}

public sealed class AiServiceConfigViewDto
{
    public string Name { get; set; } = string.Empty;
    public string PathPrefix { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>脱敏后的下游密钥</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>服务接口格式（OpenAI / Anthropic）</summary>
    public string ServiceFormat { get; set; } = "OpenAI";
    /// <summary>客户端格式（"OpenAI"/"Anthropic" 显式指定；null/"Auto" = 按鉴权头自动识别）</summary>
    public string? ClientFormat { get; set; }
    /// <summary>自定义额外请求头（如 anthropic-version）</summary>
    public Dictionary<string, string>? ExtraHeaders { get; set; }
    public bool LogRequestBody { get; set; }
    public bool LogResponseBody { get; set; }
    public bool AllowInvalidSslCertificates { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────
// 输入 DTO（管理面板 CRUD 用）
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// 新增/更新下游服务输入。
/// ApiKey 保留约定：null=保持原值（仅更新时有效，新增时按空串处理）；
/// ""=清空（不注入鉴权头，如 Ollama）；非空=设为新值。
/// </summary>
public sealed class AiServiceInputDto
{
    public string Name { get; set; } = string.Empty;
    public string PathPrefix { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>null=保持原值；""=清空；非空=设新值</summary>
    public string? ApiKey { get; set; }
    /// <summary>服务接口格式：OpenAI（默认）、Anthropic（Claude 原生）</summary>
    public string ServiceFormat { get; set; } = "OpenAI";
    /// <summary>
    /// 客户端格式。"Auto"/null/"" = 按鉴权头自动识别；
    /// "OpenAI"/"Anthropic" = 显式指定，与 ServiceFormat 不同时启用双向转换。
    /// </summary>
    public string? ClientFormat { get; set; } = "Auto";
    /// <summary>自定义额外请求头（如 anthropic-version: 2023-06-01）</summary>
    public Dictionary<string, string>? ExtraHeaders { get; set; }
    public bool LogRequestBody { get; set; } = true;
    public bool LogResponseBody { get; set; } = true;
    public bool AllowInvalidSslCertificates { get; set; } = false;
}

/// <summary>
/// 全局密钥更新输入。同 ApiKey 保留约定：
/// null=保持原值；""=关闭鉴权；非空=设新值。
/// </summary>
public sealed class GlobalApiKeyInputDto
{
    public string? ApiKey { get; set; }
}
