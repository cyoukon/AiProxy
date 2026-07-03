using System.Text.Json;

namespace AiProxy.Forwarding;

/// <summary>
/// OpenAI 兼容响应 / 请求体的字段提取工具。
/// 用于从非流式 JSON 响应中提取 usage token、从请求体中提取 model 字段。
/// 容错：响应非 JSON 或字段缺失时返回 null，不影响主链路。
/// </summary>
internal static class OpenAiParser
{
    /// <summary>从请求体 JSON 中提取 model 字段</summary>
    public static string? TryGetModel(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
            {
                return m.GetString();
            }
        }
        catch (JsonException)
        {
            // 非 JSON 请求体（如 form-encoded），忽略
        }
        return null;
    }

    /// <summary>从非流式响应 JSON 中提取 usage.token 用量（兼容 OpenAI 与 Anthropic 格式）</summary>
    public static (int? Prompt, int? Completion, int? Total) TryGetUsage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return (null, null, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                // OpenAI: prompt_tokens / completion_tokens / total_tokens
                // Anthropic: input_tokens / output_tokens（无 total，需计算）
                int? p = GetInt(usage, "prompt_tokens") ?? GetInt(usage, "promptTokens") ?? GetInt(usage, "input_tokens");
                int? c = GetInt(usage, "completion_tokens") ?? GetInt(usage, "completionTokens") ?? GetInt(usage, "output_tokens");
                int? t = GetInt(usage, "total_tokens") ?? GetInt(usage, "totalTokens");
                // Anthropic 无 total_tokens，自动累加
                if (t == null && p != null && c != null)
                {
                    t = p + c;
                }
                return (p, c, t);
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应体，忽略
        }
        return (null, null, null);
    }

    private static int? GetInt(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
        {
            return v;
        }
        return null;
    }
}
