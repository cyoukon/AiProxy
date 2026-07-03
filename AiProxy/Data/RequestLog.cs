using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AiProxy.Data;

/// <summary>
/// 单次请求的完整日志记录。对应 SQLite 表 RequestLogs。
/// 记录客户端视角与下游视角的完整信息，当存在格式转换时两侧的路径、请求体、响应体可能不同。
/// </summary>
public sealed class RequestLog
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>请求时间（UTC）</summary>
    public DateTime RequestTime { get; set; } = DateTime.UtcNow;

    /// <summary>下游服务名称</summary>
    public string ServiceName { get; set; } = string.Empty;

    // ─── 客户端侧 ───────────────────────────────────────────────────────────

    /// <summary>客户端请求的原始路径（剥离前缀后、端点映射前，如 /chat/completions 或 /messages）</summary>
    public string ClientPath { get; set; } = string.Empty;

    /// <summary>客户端请求体（原始格式，受 LogRequestBody 控制）</summary>
    public string? ClientRequestBody { get; set; }

    /// <summary>返回给客户端的响应体（转换后格式 / 无转换时同下游，受 LogResponseBody 控制）</summary>
    public string? ClientResponseBody { get; set; }

    /// <summary>客户端格式（OpenAI / Anthropic）</summary>
    public string? ClientFormat { get; set; }

    // ─── 下游侧 ─────────────────────────────────────────────────────────────

    /// <summary>下游服务的实际完整请求 URL（BaseUrl + 映射后路径 + QueryString）</summary>
    public string DownstreamUrl { get; set; } = string.Empty;

    /// <summary>发送给下游的请求体（转换后格式 / 无转换时同客户端，受 LogRequestBody 控制）</summary>
    public string? DownstreamRequestBody { get; set; }

    /// <summary>下游原始响应体（下游格式，受 LogResponseBody 控制）</summary>
    public string? DownstreamResponseBody { get; set; }

    /// <summary>下游服务格式（OpenAI / Anthropic）</summary>
    public string? ServiceFormat { get; set; }

    // ─── 通用元数据 ─────────────────────────────────────────────────────────

    /// <summary>HTTP 方法</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>响应状态码</summary>
    public int StatusCode { get; set; }

    /// <summary>总耗时（毫秒）</summary>
    public long DurationMs { get; set; }

    /// <summary>是否启用了格式转换（ClientFormat ≠ ServiceFormat）</summary>
    public bool IsConverted { get; set; }

    /// <summary>Prompt Token 用量</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Completion Token 用量</summary>
    public int? CompletionTokens { get; set; }

    /// <summary>总 Token 用量</summary>
    public int? TotalTokens { get; set; }

    /// <summary>是否成功（StatusCode == 200）</summary>
    public bool IsSuccess { get; set; }

    /// <summary>错误类型（仅失败时设置）</summary>
    public string? ErrorType { get; set; }

    /// <summary>是否为 SSE 流式响应</summary>
    public bool IsStream { get; set; }

    /// <summary>是否为重放请求</summary>
    public bool IsReplay { get; set; }

    /// <summary>模型名称（从请求体 model 字段提取）</summary>
    public string? Model { get; set; }
}
