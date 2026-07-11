using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Forwarding;

/// <summary>
/// SSE 流式响应聚合器：边解析 data: 行，将流式分片重建为完整的非流式响应 JSON，
/// 用于日志持久化与结构化展示。不保存分片数据。
///
/// 输出等价于非流式响应：
/// - OpenAI chat.completion.chunk 流 → 完整 chat.completion JSON
/// - Anthropic SSE 事件流 → 完整 message JSON
/// 同时提取 token 用量供日志元数据使用。非标准流降级为原始 data 负载拼接。
/// </summary>
internal static class SseAggregator
{
    /// <summary>
    /// 解析 SSE 字节流，重建完整响应 JSON 并提取 token 用量。
    /// fail-open：解析失败或非标准流时降级为原始负载拼接文本。
    /// </summary>
    public static (string AggregatedContent, int? PromptTokens, int? CompletionTokens, int? TotalTokens) Aggregate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return (string.Empty, null, null, null);
        }

        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');

        // 格式探测：首次遇到有效 JSON 时确定（true=Anthropic, false=OpenAI, null=未定）
        bool? isAnthropic = null;

        // ─── OpenAI 累积状态 ───
        string? oaiId = null;
        string? oaiModel = null;
        var oaiContent = new StringBuilder();
        var oaiToolCalls = new Dictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        string? oaiFinishReason = null;
        int? oaiPrompt = null;
        int? oaiCompletion = null;
        int? oaiTotal = null;

        // ─── Anthropic 累积状态 ───
        string? antId = null;
        string? antModel = null;
        var antBlocks = new Dictionary<int, BlockAccum>();
        string? antStopReason = null;
        string? antStopSequence = null;
        int? antInput = null;
        int? antOutput = null;

        // ─── 降级用：非标准流的原始负载拼接 ───
        var fallback = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // 非 data 行（event:、id:、注释）跳过
                continue;
            }

            var payload = line["data:".Length..].TrimStart();
            if (payload == "[DONE]")
            {
                continue;
            }

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                // 非 JSON 负载，作为 fallback 拼接
                fallback.AppendLine(payload);
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                bool hasType = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String;

                // 首次有效 JSON 时探测格式（流是同质的，仅探测一次）
                if (isAnthropic == null)
                {
                    if (hasType)
                    {
                        isAnthropic = true;
                    }
                    else if (root.TryGetProperty("choices", out _))
                    {
                        isAnthropic = false;
                    }
                    else
                    {
                        // 既非 Anthropic 也非 OpenAI 结构，走 fallback
                        fallback.AppendLine(payload);
                        continue;
                    }
                }

                if (isAnthropic == true)
                {
                    AccumulateAnthropic(root, typeEl.GetString()!, ref antId, ref antModel,
                        antBlocks, ref antStopReason, ref antStopSequence, ref antInput, ref antOutput);
                }
                else
                {
                    AccumulateOpenAi(root, ref oaiId, ref oaiModel, oaiContent, oaiToolCalls,
                        ref oaiFinishReason, ref oaiPrompt, ref oaiCompletion, ref oaiTotal);
                }
            }
        }

        // ─── 构建输出 ───
        if (isAnthropic == true && (antBlocks.Count > 0 || antStopReason != null || antInput != null || antOutput != null))
        {
            var total = (antInput != null && antOutput != null) ? antInput + antOutput : null;
            return (BuildAnthropicJson(antId, antModel, antBlocks, antStopReason, antStopSequence, antInput, antOutput),
                antInput, antOutput, total);
        }

        if (isAnthropic == false && (oaiContent.Length > 0 || oaiToolCalls.Count > 0 || oaiFinishReason != null || oaiPrompt != null || oaiCompletion != null))
        {
            return (BuildOpenAiJson(oaiId, oaiModel, oaiContent, oaiToolCalls, oaiFinishReason, oaiPrompt, oaiCompletion, oaiTotal),
                oaiPrompt, oaiCompletion, oaiTotal);
        }

        // 降级：非标准流，原样拼接 data 负载
        var aggregated = fallback.ToString();
        return (aggregated, null, null, null);
    }

    // ─── OpenAI chunk 累积 ───────────────────────────────────────────────────
    private static void AccumulateOpenAi(
        JsonElement root,
        ref string? id, ref string? model,
        StringBuilder content,
        Dictionary<int, (string? Id, string? Name, StringBuilder Args)> toolCalls,
        ref string? finishReason,
        ref int? prompt, ref int? completion, ref int? total)
    {
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            var s = idEl.GetString();
            if (!string.IsNullOrEmpty(s)) id = s;
        }
        if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
        {
            var s = modelEl.GetString();
            if (!string.IsNullOrEmpty(s)) model = s;
        }

        // usage（部分实现在末 chunk 携带，需 stream_options.include_usage）
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            prompt = GetInt(usage, "prompt_tokens") ?? prompt;
            completion = GetInt(usage, "completion_tokens") ?? completion;
            total = GetInt(usage, "total_tokens") ?? total;
        }

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                finishReason = fr.GetString();
            }

            if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                // content 片段
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    content.Append(c.GetString());
                }

                // tool_calls 片段
                if (delta.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in tc.EnumerateArray())
                    {
                        if (call.ValueKind != JsonValueKind.Object) continue;
                        var idx = GetInt(call, "index") ?? 0;
                        if (!toolCalls.TryGetValue(idx, out var acc))
                        {
                            acc.Args = new StringBuilder();
                            toolCalls[idx] = acc;
                        }
                        if (call.TryGetProperty("id", out var callId) && callId.ValueKind == JsonValueKind.String)
                        {
                            toolCalls[idx] = (callId.GetString(), toolCalls[idx].Name, toolCalls[idx].Args);
                        }
                        if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                        {
                            if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                            {
                                toolCalls[idx] = (toolCalls[idx].Id, name.GetString(), toolCalls[idx].Args);
                            }
                            if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                            {
                                toolCalls[idx].Args.Append(args.GetString());
                            }
                        }
                    }
                }
            }
        }
    }

    private static string BuildOpenAiJson(
        string? id, string? model,
        StringBuilder content,
        Dictionary<int, (string? Id, string? Name, StringBuilder Args)> toolCalls,
        string? finishReason,
        int? prompt, int? completion, int? total)
    {
        var message = new JsonObject { ["role"] = "assistant" };
        if (content.Length > 0)
        {
            message["content"] = content.ToString();
        }
        if (toolCalls.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var kv in toolCalls.OrderBy(kv => kv.Key))
            {
                var (tcId, tcName, tcArgs) = kv.Value;
                arr.Add(new JsonObject
                {
                    ["index"] = kv.Key,
                    ["id"] = tcId ?? "",
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tcName ?? "",
                        ["arguments"] = tcArgs.ToString()
                    }
                });
            }
            message["tool_calls"] = arr;
        }

        var choice = new JsonObject
        {
            ["index"] = 0,
            ["message"] = message,
            ["finish_reason"] = finishReason ?? "stop"
        };

        var obj = new JsonObject
        {
            ["id"] = id ?? "chatcmpl-stream",
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model ?? "",
            ["choices"] = new JsonArray { choice }
        };

        if (prompt != null || completion != null || total != null)
        {
            var usage = new JsonObject();
            if (prompt != null) usage["prompt_tokens"] = prompt;
            if (completion != null) usage["completion_tokens"] = completion;
            if (total != null) usage["total_tokens"] = total;
            obj["usage"] = usage;
        }

        return obj.ToJsonString();
    }

    // ─── Anthropic 事件累积 ──────────────────────────────────────────────────
    private static void AccumulateAnthropic(
        JsonElement root, string eventType,
        ref string? id, ref string? model,
        Dictionary<int, BlockAccum> blocks,
        ref string? stopReason, ref string? stopSequence,
        ref int? input, ref int? output)
    {
        switch (eventType)
        {
            case "message_start":
                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                {
                    if (msg.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.String)
                    {
                        id = mid.GetString();
                    }
                    if (msg.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String)
                    {
                        model = mm.GetString();
                    }
                    if (msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        input = GetInt(u, "input_tokens") ?? input;
                    }
                }
                break;

            case "content_block_start":
                if (root.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number &&
                    root.TryGetProperty("content_block", out var cb) && cb.ValueKind == JsonValueKind.Object)
                {
                    var idx = idxEl.GetInt32();
                    var block = new BlockAccum
                    {
                        Type = cb.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String ? bt.GetString()! : "text"
                    };
                    if (block.Type == "tool_use")
                    {
                        block.ToolUseId = cb.TryGetProperty("id", out var tui) && tui.ValueKind == JsonValueKind.String ? tui.GetString() : "";
                        block.ToolUseName = cb.TryGetProperty("name", out var tun) && tun.ValueKind == JsonValueKind.String ? tun.GetString() : "";
                    }
                    else if (block.Type == "redacted_thinking")
                    {
                        block.RedactedData = cb.TryGetProperty("data", out var rd) && rd.ValueKind == JsonValueKind.String ? rd.GetString() : "";
                    }
                    blocks[idx] = block;
                }
                break;

            case "content_block_delta":
                if (root.TryGetProperty("index", out var didxEl) && didxEl.ValueKind == JsonValueKind.Number &&
                    root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                    blocks.TryGetValue(didxEl.GetInt32(), out var blk))
                {
                    var deltaType = delta.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() : "";
                    if (deltaType == "text_delta" && delta.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    {
                        blk.Text.Append(txt.GetString());
                    }
                    else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj) && pj.ValueKind == JsonValueKind.String)
                    {
                        blk.InputJson.Append(pj.GetString());
                    }
                    else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String)
                    {
                        blk.Text.Append(th.GetString());
                    }
                    else if (deltaType == "signature_delta" && delta.TryGetProperty("signature", out var sig) && sig.ValueKind == JsonValueKind.String)
                    {
                        blk.Signature = sig.GetString();
                    }
                }
                break;

            case "content_block_stop":
                // 块完成，无需额外标记（重建时按已累积内容输出）
                break;

            case "message_delta":
                if (root.TryGetProperty("delta", out var md) && md.ValueKind == JsonValueKind.Object)
                {
                    if (md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                    {
                        stopReason = sr.GetString();
                    }
                    if (md.TryGetProperty("stop_sequence", out var ss) && ss.ValueKind == JsonValueKind.String)
                    {
                        stopSequence = ss.GetString();
                    }
                }
                if (root.TryGetProperty("usage", out var mu) && mu.ValueKind == JsonValueKind.Object)
                {
                    output = GetInt(mu, "output_tokens") ?? output;
                }
                break;

            case "message_stop":
                // 流结束
                break;
        }
    }

    private static string BuildAnthropicJson(
        string? id, string? model,
        Dictionary<int, BlockAccum> blocks,
        string? stopReason, string? stopSequence,
        int? input, int? output)
    {
        var content = new JsonArray();
        foreach (var kv in blocks.OrderBy(kv => kv.Key))
        {
            var b = kv.Value;
            JsonObject blockObj;
            switch (b.Type)
            {
                case "tool_use":
                    blockObj = new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = b.ToolUseId ?? "",
                        ["name"] = b.ToolUseName ?? "",
                        ["input"] = ParseInputJson(b.InputJson)
                    };
                    break;
                case "thinking":
                    blockObj = new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = b.Text.ToString()
                    };
                    if (b.Signature != null) blockObj["signature"] = b.Signature;
                    break;
                case "redacted_thinking":
                    blockObj = new JsonObject
                    {
                        ["type"] = "redacted_thinking",
                        ["data"] = b.RedactedData ?? ""
                    };
                    break;
                default: // text
                    blockObj = new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = b.Text.ToString()
                    };
                    break;
            }
            content.Add(blockObj);
        }

        var obj = new JsonObject
        {
            ["id"] = id ?? "msg_stream",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model ?? "",
            ["content"] = content,
            ["stop_reason"] = stopReason ?? "end_turn",
            ["stop_sequence"] = stopSequence
        };

        if (input != null || output != null)
        {
            var usage = new JsonObject();
            if (input != null) usage["input_tokens"] = input;
            if (output != null) usage["output_tokens"] = output;
            obj["usage"] = usage;
        }

        return obj.ToJsonString();
    }

    /// <summary>解析 tool_use 累积的 input JSON 片段；失败则返回空对象</summary>
    private static JsonNode ParseInputJson(StringBuilder sb)
    {
        var s = sb.ToString();
        if (string.IsNullOrWhiteSpace(s))
        {
            return new JsonObject();
        }
        try
        {
            return JsonNode.Parse(s) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
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

    /// <summary>Anthropic content block 累积器</summary>
    private sealed class BlockAccum
    {
        public string Type = "text";
        public StringBuilder Text = new();
        public StringBuilder InputJson = new();
        public string? ToolUseId;
        public string? ToolUseName;
        public string? Signature;
        public string? RedactedData;
    }
}
