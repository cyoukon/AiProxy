namespace AiProxy.Forwarding;

/// <summary>
/// 请求日志相关的共享辅助逻辑。
/// 业务端口日志中间件与重放（ReplayService）共用，确保错误归类与请求体判定一致（DRY）。
/// </summary>
internal static class RequestLogHelpers
{
    /// <summary>
    /// 按状态码归类错误类型。
    /// YARP IHttpForwarder 在请求超时时通常写 504 Gateway Timeout。
    /// </summary>
    public static string? ClassifyError(int statusCode)
    {
        if (statusCode == 200)
        {
            return null;
        }
        return statusCode switch
        {
            401 or 403 => "AuthFailed",
            408 or 504 => "Timeout",
            429 => "RateLimited",
            >= 500 and <= 599 => "UpstreamError",
            _ => "Other"
        };
    }

    /// <summary>判断该 HTTP 方法语义上可携带请求体</summary>
    public static bool CanHaveBody(string method)
    {
        return !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);
    }
}
