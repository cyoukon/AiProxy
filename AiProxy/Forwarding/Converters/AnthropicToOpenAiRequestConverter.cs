using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Forwarding.Converters;

/// <summary>
/// 请求转换器：Anthropic Messages 格式 → OpenAI Chat Completions 格式。
/// 使用 <see cref="JsonNode"/> 做树级改写。fail-open：解析失败或结构不符时原样返回。
/// 字段映射要点：
/// - system（string 或 text-block 数组）→ 首条 {role:system} 消息
/// - content blocks：text 保留；image→image_url；tool_use→assistant.tool_calls；tool_result→独立 {role:tool} 消息
/// - tools：{name,description,input_schema}→{type:function,function:{name,description,parameters}}
/// - tool_choice：auto→"auto"，any→"required"，tool→{type:function,function:{name}}
/// - stop_sequences→stop；丢弃 top_k/metadata；metadata.user_id→user
/// </summary>
public sealed class AnthropicToOpenAiRequestConverter : IRequestConverter
{
    /// <inheritdoc/>
    public string Convert(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return requestBody;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestBody);
        }
        catch (JsonException)
        {
            return requestBody; // 非 JSON，原样返回
        }
        if (root is not JsonObject anthropic)
        {
            return requestBody;
        }

        try
        {
            var openai = new JsonObject();

            // 直接透传的标量字段
            CopyIfPresent(anthropic, openai, "model");
            CopyIfPresent(anthropic, openai, "max_tokens");
            CopyIfPresent(anthropic, openai, "temperature");
            CopyIfPresent(anthropic, openai, "top_p");
            CopyIfPresent(anthropic, openai, "stream");
            CopyIfPresent(anthropic, openai, "n");
            CopyIfPresent(anthropic, openai, "presence_penalty");
            CopyIfPresent(anthropic, openai, "frequency_penalty");
            CopyIfPresent(anthropic, openai, "logit_bias");
            CopyIfPresent(anthropic, openai, "user");

            // metadata.user_id → user（若 user 未显式设置）
            if (!openai.ContainsKey("user") &&
                anthropic.TryGetPropertyValue("metadata", out var meta) && meta is JsonObject metaObj &&
                metaObj.TryGetPropertyValue("user_id", out var uid) && uid is not null)
            {
                openai["user"] = JsonNode.Parse(uid.ToJsonString());
            }

            // stop_sequences → stop
            if (anthropic.TryGetPropertyValue("stop_sequences", out var stopSeq) && stopSeq is not null)
            {
                openai["stop"] = JsonNode.Parse(stopSeq.ToJsonString());
            }

            // system → 首条 system 消息
            string? systemText = ExtractSystemText(anthropic);
            anthropic.Remove("system");

            // messages 转换
            var openaiMessages = new JsonArray();
            if (!string.IsNullOrEmpty(systemText))
            {
                openaiMessages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = systemText
                });
            }

            if (anthropic.TryGetPropertyValue("messages", out var msgs) && msgs is JsonArray messages)
            {
                foreach (var msg in messages)
                {
                    if (msg is not JsonObject m) continue;
                    foreach (var converted in ConvertMessage(m))
                    {
                        openaiMessages.Add(converted);
                    }
                }
            }
            openai["messages"] = openaiMessages;

            // tools 转换
            if (anthropic.TryGetPropertyValue("tools", out var tools) && tools is JsonArray toolsArr)
            {
                var openaiTools = new JsonArray();
                foreach (var t in toolsArr)
                {
                    if (t is not JsonObject tool) continue;
                    var fn = new JsonObject
                    {
                        ["name"] = tool["name"]?.DeepClone(),
                        ["description"] = tool["description"]?.DeepClone()
                    };
                    if (tool.TryGetPropertyValue("input_schema", out var schema) && schema is not null)
                    {
                        fn["parameters"] = JsonNode.Parse(schema.ToJsonString());
                    }
                    openaiTools.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = fn
                    });
                }
                openai["tools"] = openaiTools;
            }

            // tool_choice 转换
            if (anthropic.TryGetPropertyValue("tool_choice", out var tc) && tc is JsonObject tcObj)
            {
                openai["tool_choice"] = ConvertToolChoiceToOpenAi(tcObj);
            }

            return openai.ToJsonString();
        }
        catch
        {
            return requestBody; // 转换过程异常，fail-open
        }
    }

    private static string? ExtractSystemText(JsonObject anthropic)
    {
        if (!anthropic.TryGetPropertyValue("system", out var sys) || sys is null)
        {
            return null;
        }
        if (sys is JsonValue sv && sv.TryGetValue<string>(out var s))
        {
            return s;
        }
        if (sys is JsonArray arr)
        {
            // text-block 数组，拼接所有 text
            var sb = new System.Text.StringBuilder();
            foreach (var block in arr)
            {
                if (block is JsonObject b &&
                    b.TryGetPropertyValue("type", out var bt) && bt?.GetValue<string>() == "text" &&
                    b.TryGetPropertyValue("text", out var txt) && txt is not null)
                {
                    sb.Append(txt.GetValue<string>());
                }
            }
            return sb.ToString();
        }
        return null;
    }

    /// <summary>将一条 Anthropic 消息转为 0..N 条 OpenAI 消息（tool_result 会拆为独立 tool 消息）</summary>
    private static IEnumerable<JsonObject> ConvertMessage(JsonObject m)
    {
        var role = m["role"]?.GetValue<string>() ?? "user";
        var content = m["content"];

        // content 为字符串：直接映射
        if (content is JsonValue sv && sv.TryGetValue<string>(out var text))
        {
            yield return new JsonObject
            {
                ["role"] = role,
                ["content"] = text
            };
            yield break;
        }

        if (content is not JsonArray blocks)
        {
            // 无 content 或其他类型，原样保留 role + null content
            yield return new JsonObject
            {
                ["role"] = role,
                ["content"] = null
            };
            yield break;
        }

        // 分类收集 blocks
        var textImageBlocks = new JsonArray(); // OpenAI 兼容的 text/image_url blocks
        var toolCalls = new JsonArray();        // assistant 的 tool_use
        var toolResults = new List<(string toolUseId, string content)>(); // user 的 tool_result
        bool hasTextImage = false;
        bool hasToolUse = false;
        bool hasToolResult = false;

        foreach (var blk in blocks)
        {
            if (blk is not JsonObject block) continue;
            var type = block["type"]?.GetValue<string>();
            switch (type)
            {
                case "text":
                    textImageBlocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = block["text"]?.DeepClone()
                    });
                    hasTextImage = true;
                    break;
                case "image":
                    {
                        // Anthropic {type:image, source:{type:base64, media_type, data}}
                        // → OpenAI {type:image_url, image_url:{url:"data:{media_type};base64,{data}"}}
                        if (block.TryGetPropertyValue("source", out var src) && src is JsonObject srcObj &&
                            srcObj.TryGetPropertyValue("type", out var st) && st?.GetValue<string>() == "base64")
                        {
                            var mediaType = srcObj["media_type"]?.GetValue<string>() ?? "image/png";
                            var data = srcObj["data"]?.GetValue<string>() ?? "";
                            textImageBlocks.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject
                                {
                                    ["url"] = $"data:{mediaType};base64,{data}"
                                }
                            });
                            hasTextImage = true;
                        }
                        break;
                    }
                case "tool_use":
                    {
                        // → assistant tool_calls
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
                        hasToolUse = true;
                        break;
                    }
                case "tool_result":
                    {
                        // → 独立 {role:tool} 消息
                        var toolUseId = block["tool_use_id"]?.GetValue<string>() ?? "";
                        var resultContent = ExtractToolResultText(block);
                        toolResults.Add((toolUseId, resultContent));
                        hasToolResult = true;
                        break;
                    }
            }
        }

        // 1) 若有 tool_use（assistant 消息）：输出一条 assistant 消息，含 content（如有 text）+ tool_calls
        if (hasToolUse)
        {
            var am = new JsonObject { ["role"] = "assistant" };
            if (hasTextImage)
            {
                am["content"] = ExtractTextFromBlocks(textImageBlocks);
            }
            else
            {
                am["content"] = null;
            }
            am["tool_calls"] = toolCalls;
            yield return am;
            yield break;
        }

        // 2) 若有 tool_result（user 消息）：先输出各 tool 消息，再若有 text/image 输出 user 消息
        if (hasToolResult)
        {
            foreach (var (toolUseId, c) in toolResults)
            {
                yield return new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = toolUseId,
                    ["content"] = c
                };
            }
            if (hasTextImage)
            {
                yield return new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = textImageBlocks
                };
            }
            yield break;
        }

        // 3) 仅 text/image：输出 role + content blocks
        if (hasTextImage)
        {
            yield return new JsonObject
            {
                ["role"] = role,
                ["content"] = textImageBlocks
            };
            yield break;
        }

        // 空内容兜底
        yield return new JsonObject
        {
            ["role"] = role,
            ["content"] = null
        };
    }

    /// <summary>从 tool_result block 提取文本（content 为字符串或 text-block 数组）</summary>
    private static string ExtractToolResultText(JsonObject block)
    {
        if (!block.TryGetPropertyValue("content", out var c) || c is null)
        {
            return string.Empty;
        }
        if (c is JsonValue cv && cv.TryGetValue<string>(out var s))
        {
            return s;
        }
        if (c is JsonArray arr)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var b in arr)
            {
                if (b is JsonObject bo &&
                    bo.TryGetPropertyValue("type", out var bt) && bt?.GetValue<string>() == "text" &&
                    bo.TryGetPropertyValue("text", out var txt) && txt is not null)
                {
                    sb.Append(txt.GetValue<string>());
                }
            }
            return sb.ToString();
        }
        return c.ToJsonString();
    }

    /// <summary>从 text/image blocks 数组中提取纯文本（用于 assistant.tool_use 时的 content 文本）</summary>
    private static string ExtractTextFromBlocks(JsonArray blocks)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in blocks)
        {
            if (b is JsonObject bo &&
                bo.TryGetPropertyValue("type", out var bt) && bt?.GetValue<string>() == "text" &&
                bo.TryGetPropertyValue("text", out var txt) && txt is not null)
            {
                sb.Append(txt.GetValue<string>());
            }
        }
        return sb.ToString();
    }

    private static JsonNode ConvertToolChoiceToOpenAi(JsonObject tcObj)
    {
        var type = tcObj["type"]?.GetValue<string>();
        return type switch
        {
            "auto" => JsonValue.Create("auto")!,
            "any" => JsonValue.Create("required")!,
            "tool" => new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tcObj["name"]?.GetValue<string>() ?? ""
                }
            },
            "none" => JsonValue.Create("none")!,
            _ => JsonValue.Create("auto")!
        };
    }

    private static void CopyIfPresent(JsonObject src, JsonObject dst, string key)
    {
        if (src.TryGetPropertyValue(key, out var v) && v is not null)
        {
            dst[key] = JsonNode.Parse(v.ToJsonString());
        }
    }
}
