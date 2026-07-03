using System.Text;
using System.Text.Json;

namespace AiProxy.Forwarding;

/// <summary>
/// SSE 流式响应聚合器：边转发边解析 data: 行，提取聊天补全增量内容，
/// 流结束后输出完整聚合文本，用于日志持久化。不保存分片数据。
/// </summary>
internal static class SseAggregator
{
    /// <summary>
    /// 解析 SSE 字节流的聚合结果。
    /// 对于 OpenAI chat/completions 流式响应：累积每个 chunk 的 choices[0].delta.content。
    /// 对于 Anthropic Messages 流式响应：累积 content_block_delta 的 delta.text，
    ///   从 message_delta 事件提取 usage.output_tokens，从 message_start 事件提取 usage.input_tokens。
    /// 对于其他流式响应：原样拼接 data: 行负载作为 fallback。
    /// 同时尝试从最后一个含 usage 字段的 chunk 提取 token 用量。
    /// </summary>
    public static (string AggregatedContent, int? PromptTokens, int? CompletionTokens, int? TotalTokens) Aggregate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return (string.Empty, null, null, null);
        }

        var text = Encoding.UTF8.GetString(bytes);
        var contentBuilder = new StringBuilder();
        var fallbackBuilder = new StringBuilder();
        int? promptTokens = null;
        int? completionTokens = null;
        int? totalTokens = null;

        // 按 SSE 协议：事件之间以空行分隔，每行格式为 "data: <payload>"
        // payload 末尾不含 \n；data: 与负载间允许有一个空格
        var lines = text.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // 非 data 行（如 event:、id:、注释）跳过
                continue;
            }

            var payload = line["data:".Length..].TrimStart();
            if (payload == "[DONE]")
            {
                continue;
            }

            // 尝试解析为 JSON chunk
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                // 非 JSON 负载，作为 fallback 拼接
                fallbackBuilder.AppendLine(payload);
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                // ─── OpenAI 格式：usage 字段（部分实现在最后一个 chunk 携带 usage） ───
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    promptTokens = GetInt(usage, "prompt_tokens") ?? GetInt(usage, "promptTokens") ?? GetInt(usage, "input_tokens") ?? promptTokens;
                    completionTokens = GetInt(usage, "completion_tokens") ?? GetInt(usage, "completionTokens") ?? GetInt(usage, "output_tokens") ?? completionTokens;
                    totalTokens = GetInt(usage, "total_tokens") ?? GetInt(usage, "totalTokens") ?? totalTokens;
                }

                // ─── Anthropic 格式：按 type 字段分发 ───
                if (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
                {
                    var eventType = typeEl.GetString();
                    switch (eventType)
                    {
                        case "message_start":
                            // message_start.message.usage.input_tokens
                            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
                                msg.TryGetProperty("usage", out var msgUsage) && msgUsage.ValueKind == JsonValueKind.Object)
                            {
                                promptTokens = GetInt(msgUsage, "input_tokens") ?? promptTokens;
                            }
                            break;

                        case "content_block_delta":
                            // content_block_delta.delta.text
                            if (root.TryGetProperty("delta", out var cbDelta) && cbDelta.ValueKind == JsonValueKind.Object)
                            {
                                if (cbDelta.TryGetProperty("text", out var deltaText) && deltaText.ValueKind == JsonValueKind.String)
                                {
                                    contentBuilder.Append(deltaText.GetString());
                                }
                            }
                            break;

                        case "message_delta":
                            // message_delta.usage.output_tokens
                            if (root.TryGetProperty("usage", out var mdUsage) && mdUsage.ValueKind == JsonValueKind.Object)
                            {
                                completionTokens = GetInt(mdUsage, "output_tokens") ?? completionTokens;
                            }
                            break;
                    }

                    // Anthropic 格式已处理，跳过 OpenAI choices 解析
                    continue;
                }

                // ─── OpenAI 格式：choices[0].delta.content ───
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                        {
                            contentBuilder.Append(contentEl.GetString());
                        }
                    }
                    // 兼容 completions 接口的 text 字段（非 chat）
                    if (firstChoice.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    {
                        contentBuilder.Append(textEl.GetString());
                    }
                }
            }
        }

        var aggregated = contentBuilder.ToString();
        if (aggregated.Length == 0 && fallbackBuilder.Length > 0)
        {
            // 非 OpenAI/Anthropic 标准 chunk，原样拼接 data: 负载
            aggregated = fallbackBuilder.ToString();
        }

        // Anthropic 无 total_tokens，自动累加
        if (totalTokens == null && promptTokens != null && completionTokens != null)
        {
            totalTokens = promptTokens + completionTokens;
        }

        return (aggregated, promptTokens, completionTokens, totalTokens);
    }

    private static int? GetInt(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt32(out var v))
            {
                return v;
            }
        }
        return null;
    }
}
