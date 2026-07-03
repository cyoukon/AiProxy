using System.Security.Cryptography;
using System.Text;
using AiProxy.Config;
using AiProxy.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Middleware;

/// <summary>
/// 全局代理访问鉴权中间件（业务分支与管理分支共用）。
/// 行为：
/// - GlobalApiKey 为空：跳过鉴权（本地无鉴权）。
/// - GlobalApiKey 非空：校验客户端 Authorization 头，接受 "Bearer &lt;key&gt;" 或裸 "&lt;key&gt;" 两种形式；
///   不匹配则立即返回 401，不进行上游转发。
/// 通过 IOptionsMonitor&lt;AppOptions&gt; 读取（Singleton 安全），密钥变更对新请求即时生效。
/// </summary>
public sealed class GlobalAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<AppOptions> _options;
    private readonly ILogger<GlobalAuthMiddleware> _logger;

    public GlobalAuthMiddleware(RequestDelegate next, IOptionsMonitor<AppOptions> options, ILogger<GlobalAuthMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var globalKey = _options.CurrentValue.Proxy.GlobalApiKey;
        if (string.IsNullOrEmpty(globalKey))
        {
            // 全局鉴权关闭：直接放行
            await _next(context);
            return;
        }

        // 管理面板 HTML 外壳与 favicon 免鉴权（登录页模式）：
        // 浏览器地址栏导航无法携带自定义 Authorization 头，若对 GET / 也鉴权，页面直接 401，
        // 用户永远看不到密钥输入框。HTML 外壳不含任何敏感数据（密钥全脱敏，仅在带鉴权的
        // /api/* 调用中返回），故放行外壳加载；前端拿到页面后通过 fetch 携带 Bearer Key
        // 访问 /api/*（仍受下方鉴权保护）。
        // /admin/* 为静态资源（CSS/JS），同样需免鉴权加载。
        if (context.Request.Method == "GET"
            && (context.Request.Path == "/" || context.Request.Path == "/favicon.ico"
                || context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var provided = ExtractKey(context.Request.Headers);
        if (!string.IsNullOrEmpty(provided) && ConstantTimeEquals(provided, globalKey))
        {
            await _next(context);
            return;
        }

        // 鉴权失败：立即 401，不转发上游。日志输出脱敏失败摘要。
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        var remote = context.Connection.RemoteIpAddress;
        _logger.LogWarning("AUTH_DENIED port={Port} remote={Remote} provided={Provided}",
            context.Connection.LocalPort, remote, KeyMasker.Mask(provided));
        await context.Response.WriteAsync("{\"error\":{\"message\":\"Invalid proxy API key\",\"type\":\"ProxyAuthFailed\"}}");
    }

    /// <summary>
    /// 从请求头提取代理层鉴权 key。
    /// 支持多种客户端格式：
    /// - Authorization: Bearer &lt;key&gt;（OpenAI SDK 等）
    /// - Authorization: &lt;key&gt;（裸 key）
    /// - x-api-key: &lt;key&gt;（Anthropic SDK）
    /// 优先检查 Authorization 头，若为空则回退到 x-api-key 头。
    /// </summary>
    private static string ExtractKey(IHeaderDictionary headers)
    {
        // 优先从 Authorization 头提取
        var authHeader = headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader))
        {
            var span = authHeader.AsSpan().Trim();
            if (span.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return span["Bearer ".Length..].Trim().ToString();
            }
            return span.ToString();
        }

        // 回退：Anthropic 客户端使用 x-api-key 头
        var xApiKey = headers["x-api-key"].ToString();
        if (!string.IsNullOrEmpty(xApiKey))
        {
            return xApiKey.Trim();
        }

        return string.Empty;
    }

    /// <summary>常数时间字符串比较，避免时序侧信道（包括长度信息泄露）</summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
