using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Forwarding.Converters;

/// <summary>
/// 响应转换器：OpenAI Chat Completions 格式 → Anthropic Messages 格式。
/// 实现 <see cref="INonStreamingResponseConverter"/>（整体 JSON 转换）+
/// 提供 <see cref="CreateStreaming"/> 返回流式状态机 <see cref="StreamingConverter"/>。
/// fail-open：解析失败或结构不符时原样返回。
/// </summary>
public sealed class OpenAiToAnthropicResponseConverter : INonStreamingResponseConverter
{
    /// <inheritdoc/>
    public string Convert(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return responseBody;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(responseBody);
        }
        catch (JsonException)
        {
            return responseBody;
        }
        if (root is not JsonObject openai)
        {
            return responseBody;
        }

        try
        {
            var anthropic = new JsonObject
            {
                ["id"] = openai["id"]?.DeepClone() ?? "msg_openai",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = openai["model"]?.DeepClone() ?? "openai"
            };

            // choices[0].message → content blocks
            var contentBlocks = new JsonArray();
            string? finishReason = null;

            if (openai.TryGetPropertyValue("choices", out var ch) && ch is JsonArray choices && choices.Count > 0)
            {
                var firstChoice = choices[0] as JsonObject;
                if (firstChoice is not null)
                {
                    finishReason = firstChoice["finish_reason"]?.GetValue<string>();
                    if (firstChoice.TryGetPropertyValue("message", out var msgNode) && msgNode is JsonObject message)
                    {
                        // content（string 或 array）
                        if (message.TryGetPropertyValue("content", out var c) && c is not null)
                        {
                            if (c is JsonValue cv && cv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                            {
                                contentBlocks.Add(new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = s
                                });
                            }
                            else if (c is JsonArray arr)
                            {
                                foreach (var b in arr)
                                {
                                    if (b is JsonObject bo && bo["type"]?.GetValue<string>() == "text")
                                    {
                                        contentBlocks.Add(new JsonObject
                                        {
                                            ["type"] = "text",
                                            ["text"] = bo["text"]?.DeepClone()
                                        });
                                    }
                                }
                            }
                        }

                        // tool_calls → tool_use blocks
                        if (message.TryGetPropertyValue("tool_calls", out var tc) && tc is JsonArray tcArr)
                        {
                            foreach (var call in tcArr)
                            {
                                if (call is not JsonObject tcObj) continue;
                                var fn = tcObj["function"] as JsonObject;
                                var id = tcObj["id"]?.GetValue<string>() ?? "";
                                var name = fn?["name"]?.GetValue<string>() ?? "";
                                var argumentsStr = fn?["arguments"]?.GetValue<string>() ?? "{}";
                                JsonNode? input;
                                try
                                {
                                    input = JsonNode.Parse(argumentsStr);
                                }
                                catch (JsonException)
                                {
                                    input = new JsonObject();
                                }
                                contentBlocks.Add(new JsonObject
                                {
                                    ["type"] = "tool_use",
                                    ["id"] = id,
                                    ["name"] = name,
                                    ["input"] = input
                                });
                            }
                        }
                    }
                }
            }

            if (contentBlocks.Count == 0)
            {
                contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = "" });
            }
            anthropic["content"] = contentBlocks;
            anthropic["stop_reason"] = FinishReasonToStopReason(finishReason);

            // usage：prompt_tokens→input_tokens, completion_tokens→output_tokens
            if (openai.TryGetPropertyValue("usage", out var u) && u is JsonObject usage)
            {
                var input = GetInt(usage, "prompt_tokens");
                var output = GetInt(usage, "completion_tokens");
                var usageObj = new JsonObject();
                if (input.HasValue) usageObj["input_tokens"] = input.Value;
                if (output.HasValue) usageObj["output_tokens"] = output.Value;
                anthropic["usage"] = usageObj;
            }

            return anthropic.ToJsonString();
        }
        catch
        {
            return responseBody;
        }
    }

    /// <summary>创建流式转换器实例（每请求一个，有状态）</summary>
    public IStreamingResponseConverter CreateStreaming() => new StreamingConverter();

    /// <summary>OpenAI finish_reason → Anthropic stop_reason</summary>
    private static string FinishReasonToStopReason(string? finishReason) => finishReason switch
    {
        "stop" => "end_turn",
        "length" => "max_tokens",
        "tool_calls" => "tool_use",
        "function_call" => "tool_use",
        "content_filter" => "refusal",
        _ => "end_turn"
    };

    private static int? GetInt(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var el) && el is not null)
        {
            return ReadInt(el);
        }
        return null;
    }

    private static int? ReadInt(JsonNode el)
    {
        var v = el.AsValue();
        if (v.TryGetValue<int>(out var iv)) return iv;
        if (v.TryGetValue<long>(out var lv)) return (int)lv;
        return null;
    }

    /// <summary>
    /// 流式转换器：OpenAI chat.completion.chunk → Anthropic SSE 事件。
    /// 状态机维护 message id/model、当前 content block 索引、text block 开闭、
    /// OpenAI tool_call index → Anthropic block index 映射、usage、stop_reason。
    /// </summary>
    private sealed class StreamingConverter : IStreamingResponseConverter
    {
        private readonly SseFrameReader _reader = new();
        private string _messageId = "msg_openai";
        private string _model = "openai";
        private bool _messageStarted;
        private int _nextBlockIndex; // 下一个 Anthropic content block 索引
        private int? _openTextBlockIndex; // 当前打开的 text block 索引（未发 content_block_stop）
        private readonly Dictionary<int, int> _openAIToolIndexToBlockIndex = new();
        private int? _inputTokens;
        private int? _outputTokens;
        private string? _finishReason;
        private bool _stopped;

        public byte[] Process(ReadOnlySpan<byte> downstreamChunk)
        {
            var events = _reader.Feed(downstreamChunk);
            var output = new StringBuilder();
            foreach (var ev in events)
            {
                HandleEvent(ev, output);
            }
            return Encoding.UTF8.GetBytes(output.ToString());
        }

        public byte[] Flush()
        {
            var events = _reader.Flush();
            var output = new StringBuilder();
            foreach (var ev in events)
            {
                HandleEvent(ev, output);
            }
            EnsureStopped(output);
            return Encoding.UTF8.GetBytes(output.ToString());
        }

        private void HandleEvent(SseEvent ev, StringBuilder output)
        {
            if (ev.IsDone)
            {
                EnsureStopped(output);
                return;
            }

            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(ev.Data) as JsonObject;
            }
            catch (JsonException)
            {
                return;
            }
            if (obj is null) return;

            // 提取 usage（部分实现在末 chunk 携带）
            if (obj.TryGetPropertyValue("usage", out var u) && u is JsonObject usage)
            {
                _inputTokens = GetInt(usage, "prompt_tokens") ?? _inputTokens;
                _outputTokens = GetInt(usage, "completion_tokens") ?? _outputTokens;
            }

            if (obj["id"] is JsonValue idVal && idVal.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id))
            {
                _messageId = id;
            }
            if (obj["model"] is JsonValue modelVal && modelVal.TryGetValue<string>(out var model) && !string.IsNullOrEmpty(model))
            {
                _model = model;
            }

            // choices
            if (obj.TryGetPropertyValue("choices", out var ch) && ch is JsonArray choices && choices.Count > 0)
            {
                var firstChoice = choices[0] as JsonObject;
                if (firstChoice is null) return;

                var delta = firstChoice["delta"] as JsonObject;
                var finishReason = firstChoice["finish_reason"]?.GetValue<string>();

                if (delta is not null)
                {
                    EnsureMessageStarted(output);

                    // role（首 chunk）
                    if (delta.TryGetPropertyValue("role", out var role) && role is not null)
                    {
                        // 已由 EnsureMessageStarted 处理 message_start，此处无需额外输出
                    }

                    // content
                    if (delta.TryGetPropertyValue("content", out var c) && c is JsonValue cv &&
                        cv.TryGetValue<string>(out var text) && text.Length > 0)
                    {
                        EnsureTextBlockOpen(output);
                        EmitEvent("content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = _openTextBlockIndex ?? 0,
                            ["delta"] = new JsonObject
                            {
                                ["type"] = "text_delta",
                                ["text"] = text
                            }
                        }, output);
                    }

                    // tool_calls
                    if (delta.TryGetPropertyValue("tool_calls", out var tc) && tc is JsonArray tcArr)
                    {
                        foreach (var call in tcArr)
                        {
                            if (call is not JsonObject tcObj) continue;
                            HandleToolCall(tcObj, output);
                        }
                    }
                }

                if (finishReason is not null)
                {
                    _finishReason = finishReason;
                    // 收尾：关闭当前 block + message_delta + message_stop
                    CloseOpenBlock(output);
                    EmitEvent("message_delta", new JsonObject
                    {
                        ["type"] = "message_delta",
                        ["delta"] = new JsonObject
                        {
                            ["stop_reason"] = FinishReasonToStopReason(finishReason)
                        },
                        ["usage"] = BuildUsageForMessageDelta()
                    }, output);
                    EmitMessageStop(output);
                    _stopped = true;
                }
            }
        }

        private void HandleToolCall(JsonObject tcObj, StringBuilder output)
        {
            var oaiIndex = tcObj["index"] is { } idxNode ? ReadInt(idxNode) ?? 0 : 0;
            var fn = tcObj["function"] as JsonObject;

            if (!_openAIToolIndexToBlockIndex.TryGetValue(oaiIndex, out var blockIndex))
            {
                // 新 tool_call：关闭当前 text block，开 tool_use block
                CloseOpenBlock(output);
                blockIndex = _nextBlockIndex++;
                _openAIToolIndexToBlockIndex[oaiIndex] = blockIndex;

                var id = tcObj["id"]?.GetValue<string>() ?? "";
                var name = fn?["name"]?.GetValue<string>() ?? "";
                EmitEvent("content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = blockIndex,
                    ["content_block"] = new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = id,
                        ["name"] = name,
                        ["input"] = new JsonObject()
                    }
                }, output);
            }

            // arguments 片段 → input_json_delta
            if (fn is not null && fn.TryGetPropertyValue("arguments", out var argsNode) && argsNode is not null)
            {
                var partialJson = argsNode.GetValue<string>();
                if (!string.IsNullOrEmpty(partialJson))
                {
                    EmitEvent("content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = blockIndex,
                        ["delta"] = new JsonObject
                        {
                            ["type"] = "input_json_delta",
                            ["partial_json"] = partialJson
                        }
                    }, output);
                }
            }
        }

        private void EnsureMessageStarted(StringBuilder output)
        {
            if (_messageStarted) return;
            _messageStarted = true;
            EmitEvent("message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["id"] = _messageId,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = _model,
                    ["content"] = new JsonArray(),
                    ["stop_reason"] = null,
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = _inputTokens ?? 0,
                        ["output_tokens"] = _outputTokens ?? 0
                    }
                }
            }, output);
        }

        private void EnsureTextBlockOpen(StringBuilder output)
        {
            if (_openTextBlockIndex.HasValue) return;
            // 若当前有 tool block 打开，不应再开 text block（Anthropic block 顺序约束）；
            // 实际 OpenAI 不会在 tool_calls 后再发 content，此处兜底不再开
            if (_openAIToolIndexToBlockIndex.Count > 0) return;

            _openTextBlockIndex = _nextBlockIndex++;
            EmitEvent("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = _openTextBlockIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ""
                }
            }, output);
        }

        private void CloseOpenBlock(StringBuilder output)
        {
            if (_openTextBlockIndex.HasValue)
            {
                EmitEvent("content_block_stop", new JsonObject
                {
                    ["type"] = "content_block_stop",
                    ["index"] = _openTextBlockIndex
                }, output);
                _openTextBlockIndex = null;
            }
            // 关闭所有打开的 tool_use block
            foreach (var (_, blockIndex) in _openAIToolIndexToBlockIndex)
            {
                EmitEvent("content_block_stop", new JsonObject
                {
                    ["type"] = "content_block_stop",
                    ["index"] = blockIndex
                }, output);
            }
            _openAIToolIndexToBlockIndex.Clear();
        }

        private JsonObject BuildUsageForMessageDelta()
        {
            var u = new JsonObject();
            if (_outputTokens.HasValue) u["output_tokens"] = _outputTokens.Value;
            if (_inputTokens.HasValue) u["input_tokens"] = _inputTokens.Value;
            return u;
        }

        private void EmitMessageStop(StringBuilder output)
        {
            EmitEvent("message_stop", new JsonObject { ["type"] = "message_stop" }, output);
        }

        /// <summary>若尚未停止，补发收尾事件（下游未发 finish_reason/[DONE] 时的兜底）</summary>
        private void EnsureStopped(StringBuilder output)
        {
            if (_stopped) return;
            if (!_messageStarted)
            {
                // 从未开始：发空 message_start
                EnsureMessageStarted(output);
            }
            CloseOpenBlock(output);
            EmitEvent("message_delta", new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject { ["stop_reason"] = FinishReasonToStopReason(_finishReason) },
                ["usage"] = BuildUsageForMessageDelta()
            }, output);
            EmitMessageStop(output);
            _stopped = true;
        }

        private void EmitEvent(string eventName, JsonObject payload, StringBuilder output)
        {
            output.Append("event: ").Append(eventName).Append('\n');
            output.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");
        }

        private static int? GetInt(JsonObject obj, string name)
        {
            if (obj.TryGetPropertyValue(name, out var el) && el is not null)
            {
                return ReadInt(el);
            }
            return null;
        }

        public void Dispose()
        {
            // 无需释放资源
        }
    }
}
