using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Forwarding.Converters;

/// <summary>
/// 响应转换器：Anthropic Messages 格式 → OpenAI Chat Completions 格式。
/// 实现 <see cref="INonStreamingResponseConverter"/>（整体 JSON 转换）+
/// 提供 <see cref="CreateStreaming"/> 返回流式状态机 <see cref="StreamingConverter"/>。
/// fail-open：解析失败或结构不符时原样返回。
/// </summary>
public sealed class AnthropicToOpenAiResponseConverter : INonStreamingResponseConverter
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
        if (root is not JsonObject anthropic)
        {
            return responseBody;
        }

        try
        {
            var openai = new JsonObject
            {
                ["id"] = anthropic["id"]?.DeepClone() ?? "chatcmpl-anthropic",
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = anthropic["model"]?.DeepClone() ?? "anthropic"
            };

            // content blocks → message.content + message.tool_calls
            string? textContent = null;
            JsonArray? toolCalls = null;
            if (anthropic.TryGetPropertyValue("content", out var c) && c is JsonArray blocks)
            {
                var sb = new StringBuilder();
                foreach (var b in blocks)
                {
                    if (b is not JsonObject block) continue;
                    var type = block["type"]?.GetValue<string>();
                    switch (type)
                    {
                        case "text":
                            sb.Append(block["text"]?.GetValue<string>() ?? "");
                            break;
                        case "tool_use":
                            {
                                toolCalls ??= new JsonArray();
                                var id = block["id"]?.GetValue<string>() ?? "";
                                var name = block["name"]?.GetValue<string>() ?? "";
                                var input = block["input"];
                                var arguments = input is null ? "{}" : input.ToJsonString();
                                toolCalls.Add(new JsonObject
                                {
                                    ["id"] = id,
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = name,
                                        ["arguments"] = arguments
                                    }
                                });
                                break;
                            }
                    }
                }
                if (sb.Length > 0)
                {
                    textContent = sb.ToString();
                }
            }

            var message = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = textContent
            };
            if (toolCalls is not null)
            {
                message["tool_calls"] = toolCalls;
            }

            var stopReason = anthropic["stop_reason"]?.GetValue<string>();
            openai["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = StopReasonToFinishReason(stopReason)
                }
            };

            // usage：input_tokens→prompt_tokens, output_tokens→completion_tokens
            if (anthropic.TryGetPropertyValue("usage", out var u) && u is JsonObject usage)
            {
                var prompt = GetInt(usage, "input_tokens");
                var completion = GetInt(usage, "output_tokens");
                var usageObj = new JsonObject();
                if (prompt.HasValue) usageObj["prompt_tokens"] = prompt.Value;
                if (completion.HasValue) usageObj["completion_tokens"] = completion.Value;
                if (prompt.HasValue && completion.HasValue)
                {
                    usageObj["total_tokens"] = prompt.Value + completion.Value;
                }
                openai["usage"] = usageObj;
            }

            return openai.ToJsonString();
        }
        catch
        {
            return responseBody;
        }
    }

    /// <summary>创建流式转换器实例（每请求一个，有状态）</summary>
    public IStreamingResponseConverter CreateStreaming() => new StreamingConverter();

    /// <summary>Anthropic stop_reason → OpenAI finish_reason</summary>
    private static string StopReasonToFinishReason(string? stopReason) => stopReason switch
    {
        "end_turn" => "stop",
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        "stop_sequence" => "stop",
        _ => "stop"
    };

    private static int? GetInt(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var el) && el is not null)
        {
            return ReadInt(el);
        }
        return null;
    }

    /// <summary>从 JsonNode 读取整数（兼容 int/long）</summary>
    private static int? ReadInt(JsonNode el)
    {
        var v = el.AsValue();
        if (v.TryGetValue<int>(out var iv)) return iv;
        if (v.TryGetValue<long>(out var lv)) return (int)lv;
        return null;
    }

    /// <summary>
    /// 流式转换器：Anthropic SSE 事件 → OpenAI chat.completion.chunk 事件。
    /// 状态机维护 message id/model、input_tokens、output_tokens、stop_reason、
    /// content_block index → OpenAI tool_call index 映射。
    /// </summary>
    private sealed class StreamingConverter : IStreamingResponseConverter
    {
        private readonly SseFrameReader _reader = new();
        private string _messageId = "chatcmpl-anthropic";
        private string _model = "anthropic";
        private int? _inputTokens;
        private int? _outputTokens;
        private string? _stopReason;
        private readonly Dictionary<int, int> _blockIndexToToolCallIndex = new();
        private int _toolCallCount;
        private bool _done;

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
            if (!_done)
            {
                // 下游未发送 message_stop，补发终止
                output.Append("data: [DONE]\n\n");
                _done = true;
            }
            return Encoding.UTF8.GetBytes(output.ToString());
        }

        private void HandleEvent(SseEvent ev, StringBuilder output)
        {
            if (ev.IsDone)
            {
                if (!_done)
                {
                    output.Append("data: [DONE]\n\n");
                    _done = true;
                }
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

            var type = obj["type"]?.GetValue<string>();
            switch (type)
            {
                case "message_start":
                    {
                        var message = obj["message"] as JsonObject;
                        if (message is not null)
                        {
                            _messageId = message["id"]?.GetValue<string>() ?? _messageId;
                            _model = message["model"]?.GetValue<string>() ?? _model;
                            if (message.TryGetPropertyValue("usage", out var u) && u is JsonObject usage)
                            {
                                _inputTokens = GetInt(usage, "input_tokens") ?? _inputTokens;
                            }
                        }
                        // 发首个 chunk：role:assistant
                        var chunk = new JsonObject
                        {
                            ["id"] = _messageId,
                            ["object"] = "chat.completion.chunk",
                            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["model"] = _model,
                            ["choices"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["index"] = 0,
                                    ["delta"] = new JsonObject { ["role"] = "assistant" },
                                    ["finish_reason"] = null
                                }
                            }
                        };
                        EmitChunk(chunk, output);
                        break;
                    }
                case "content_block_start":
                    {
                        var index = obj["index"] is { } idxNode ? ReadInt(idxNode) ?? 0 : 0;
                        var contentBlock = obj["content_block"] as JsonObject;
                        var blockType = contentBlock?["type"]?.GetValue<string>();
                        if (blockType == "tool_use")
                        {
                            var tcIndex = _toolCallCount++;
                            _blockIndexToToolCallIndex[index] = tcIndex;
                            var id = contentBlock!["id"]?.GetValue<string>() ?? "";
                            var name = contentBlock["name"]?.GetValue<string>() ?? "";
                            var chunk = new JsonObject
                            {
                                ["id"] = _messageId,
                                ["object"] = "chat.completion.chunk",
                                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                ["model"] = _model,
                                ["choices"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["index"] = 0,
                                        ["delta"] = new JsonObject
                                        {
                                            ["tool_calls"] = new JsonArray
                                            {
                                                new JsonObject
                                                {
                                                    ["index"] = tcIndex,
                                                    ["id"] = id,
                                                    ["type"] = "function",
                                                    ["function"] = new JsonObject { ["name"] = name }
                                                }
                                            }
                                        },
                                        ["finish_reason"] = null
                                    }
                                }
                            };
                            EmitChunk(chunk, output);
                        }
                        break;
                    }
                case "content_block_delta":
                    {
                        var delta = obj["delta"] as JsonObject;
                        var deltaType = delta?["type"]?.GetValue<string>();
                        if (deltaType == "text_delta")
                        {
                            var text = delta!["text"]?.GetValue<string>() ?? "";
                            var chunk = MakeDeltaChunk(new JsonObject { ["content"] = text });
                            EmitChunk(chunk, output);
                        }
                        else if (deltaType == "input_json_delta")
                        {
                            var index = obj["index"] is { } idxNode ? ReadInt(idxNode) ?? 0 : 0;
                            var partialJson = delta!["partial_json"]?.GetValue<string>() ?? "";
                            if (_blockIndexToToolCallIndex.TryGetValue(index, out var tcIndex))
                            {
                                var chunk = MakeDeltaChunk(new JsonObject
                                {
                                    ["tool_calls"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["index"] = tcIndex,
                                            ["function"] = new JsonObject { ["arguments"] = partialJson }
                                        }
                                    }
                                });
                                EmitChunk(chunk, output);
                            }
                        }
                        break;
                    }
                case "content_block_stop":
                    // 无输出
                    break;
                case "message_delta":
                    {
                        var delta = obj["delta"] as JsonObject;
                        if (delta is not null && delta.TryGetPropertyValue("stop_reason", out var sr) && sr is not null)
                        {
                            _stopReason = sr.GetValue<string>();
                        }
                        if (obj.TryGetPropertyValue("usage", out var u) && u is JsonObject usage)
                        {
                            _outputTokens = GetInt(usage, "output_tokens") ?? _outputTokens;
                        }
                        // 发末 chunk：finish_reason + usage
                        var finalDelta = new JsonObject();
                        var chunk = new JsonObject
                        {
                            ["id"] = _messageId,
                            ["object"] = "chat.completion.chunk",
                            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["model"] = _model,
                            ["choices"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["index"] = 0,
                                    ["delta"] = finalDelta,
                                    ["finish_reason"] = StopReasonToFinishReason(_stopReason)
                                }
                            }
                        };
                        // 附加 usage（OpenAI 在末 chunk 携带 usage）
                        if (_inputTokens.HasValue || _outputTokens.HasValue)
                        {
                            var usageObj = new JsonObject();
                            if (_inputTokens.HasValue) usageObj["prompt_tokens"] = _inputTokens.Value;
                            if (_outputTokens.HasValue) usageObj["completion_tokens"] = _outputTokens.Value;
                            if (_inputTokens.HasValue && _outputTokens.HasValue)
                            {
                                usageObj["total_tokens"] = _inputTokens.Value + _outputTokens.Value;
                            }
                            chunk["usage"] = usageObj;
                        }
                        EmitChunk(chunk, output);
                        break;
                    }
                case "message_stop":
                    output.Append("data: [DONE]\n\n");
                    _done = true;
                    break;
            }
        }

        private JsonObject MakeDeltaChunk(JsonObject delta)
        {
            return new JsonObject
            {
                ["id"] = _messageId,
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = _model,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = delta,
                        ["finish_reason"] = null
                    }
                }
            };
        }

        private static void EmitChunk(JsonObject chunk, StringBuilder output)
        {
            output.Append("data: ").Append(chunk.ToJsonString()).Append("\n\n");
        }

        private static int? GetInt(JsonObject obj, string name)
        {
            if (obj.TryGetPropertyValue(name, out var el) && el is not null)
            {
                var v = el.AsValue();
                if (v.TryGetValue<int>(out var iv)) return iv;
                if (v.TryGetValue<long>(out var lv)) return (int)lv;
            }
            return null;
        }

        public void Dispose()
        {
            // 无需释放资源
        }
    }
}
