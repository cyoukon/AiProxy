using AiProxy.Config;
using Microsoft.AspNetCore.Http;

namespace AiProxy.Forwarding.Converters;

/// <summary>
/// 端点路径映射：客户端格式与下游格式不同时，将请求路径中的已知 API 端点重写为下游对应端点。
///
/// 背景：<see cref="IRequestConverter"/>/<see cref="INonStreamingResponseConverter"/> 等转换器只处理
/// 请求/响应体，不涉及 URL 路径。但 OpenAI 与 Anthropic 的端点路径本身不同（如 chat/completions
/// vs messages）——仅转换 body 而不重写路径，请求会被转发到下游不存在的路径，直接 404。
///
/// 设计：匹配剩余路径的**尾段**（末端端点名），不依赖版本号前缀是否存在于路径中。
/// 这是因为不同服务商的 BaseUrl 约定不同：
/// - origin-only（如 https://open.bigmodel.cn/api/paas）→ 客户端路径带 /v1/chat/completions
/// - 含版本号（如 https://api.openai.com/v1）→ 客户端路径仅为 /chat/completions
/// 映射表按叶子段匹配并保留原始前缀部分（包括 /v1 或其他 prefix），仅替换端点尾段。
///
/// 仅重写已知的一一对应端点；无法识别的路径原样透传（fail-open）。
/// 两侧共有的端点（如 /models）不在映射表中，天然原样透传。
/// </summary>
internal static class EndpointPathMapper
{
    // ────────────────────────────────────────────────────────────────────────
    // 端点尾段映射表：只关心路径尾端的端点名（不含前导版本号）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Anthropic 端点尾段 → OpenAI 端点尾段</summary>
    private static readonly Dictionary<string, string> AnthropicTailToOpenAi = new(StringComparer.OrdinalIgnoreCase)
    {
        ["messages"] = "chat/completions",
    };

    /// <summary>OpenAI 端点尾段 → Anthropic 端点尾段</summary>
    private static readonly Dictionary<string, string> OpenAiTailToAnthropic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chat/completions"] = "messages",
    };

    /// <summary>
    /// 按 (客户端格式, 下游格式) 重写剥离前缀后的剩余路径。
    /// 格式相同（identity）或路径未命中已知端点表时原样返回。
    ///
    /// 匹配策略：从路径尾端匹配已知端点名（支持单段如 "messages"、多段如 "chat/completions"），
    /// 命中后保留前导部分（如 /v1/）不变，仅替换尾段。
    /// 例：/v1/messages → /v1/chat/completions，/messages → /chat/completions
    /// </summary>
    public static PathString Map(PathString remainingPath, ServiceFormat clientFormat, ServiceFormat serviceFormat)
    {
        if (clientFormat == serviceFormat)
        {
            return remainingPath;
        }

        var path = remainingPath.Value;
        if (string.IsNullOrEmpty(path))
        {
            return remainingPath;
        }

        var table = (clientFormat, serviceFormat) switch
        {
            (ServiceFormat.Anthropic, ServiceFormat.OpenAI) => AnthropicTailToOpenAi,
            (ServiceFormat.OpenAI, ServiceFormat.Anthropic) => OpenAiTailToAnthropic,
            _ => null
        };
        if (table is null)
        {
            return remainingPath;
        }

        // 去除首尾斜杠以便统一处理
        // 例："/v1/chat/completions" → "v1/chat/completions"
        var trimmed = path.TrimStart('/').TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return remainingPath;
        }

        // 遍历映射表，检查 trimmed 是否以某个 key 结尾（含完全等于 key 的情况）
        foreach (var (tail, replacement) in table)
        {
            if (trimmed.Equals(tail, StringComparison.OrdinalIgnoreCase))
            {
                // 完全等于 key：替换整个路径
                return new PathString("/" + replacement);
            }

            // 以 "/key" 结尾：保留前导部分 + 替换尾段
            var suffix = "/" + tail;
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var prefix = trimmed[..^suffix.Length];
                return new PathString("/" + prefix + "/" + replacement);
            }
        }

        return remainingPath;
    }
}
