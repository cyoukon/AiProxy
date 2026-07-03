using AiProxy.Config;
using Microsoft.AspNetCore.Http;

namespace AiProxy.Forwarding.Converters;

/// <summary>
/// 解析当前请求生效的客户端格式。
/// <see cref="AiServiceOptions.ClientFormat"/> 为 null 时启用自动模式：
/// 按请求鉴权头逐请求推断（Authorization: Bearer → OpenAI，x-api-key → Anthropic，均无 → OpenAI）。
/// </summary>
internal static class ClientFormatResolver
{
    /// <summary>
    /// 解析生效客户端格式。有 HttpContext 时按鉴权头推断 Auto；显式配置直接返回。
    /// </summary>
    public static ServiceFormat Resolve(AiServiceOptions service, HttpContext context)
    {
        if (service.ClientFormat.HasValue)
        {
            return service.ClientFormat.Value;
        }
        return DetectFromHeaders(context);
    }

    /// <summary>
    /// 无 HttpContext 场景（如重放）：显式配置直接返回，Auto fallback OpenAI。
    /// 重放无法拿到原始客户端鉴权头，Auto 近似为 OpenAI（业界最常见格式）。
    /// </summary>
    public static ServiceFormat Resolve(AiServiceOptions service)
        => service.ClientFormat ?? ServiceFormat.OpenAI;

    /// <summary>
    /// 按请求鉴权头推断客户端格式：
    /// - x-api-key 非空 → Anthropic（Claude 原生客户端）
    /// - Authorization: Bearer 非空 → OpenAI
    /// - 均无 → OpenAI（默认）
    /// 优先 x-api-key：Anthropic 客户端必带该头，而部分 OpenAI 客户端可能同时带其他头。
    /// </summary>
    private static ServiceFormat DetectFromHeaders(HttpContext context)
    {
        var headers = context.Request.Headers;
        if (headers.TryGetValue("x-api-key", out var xKey) && !string.IsNullOrEmpty(xKey.ToString()))
        {
            return ServiceFormat.Anthropic;
        }
        if (headers.TryGetValue("Authorization", out var auth)
            && auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceFormat.OpenAI;
        }
        return ServiceFormat.OpenAI;
    }
}
