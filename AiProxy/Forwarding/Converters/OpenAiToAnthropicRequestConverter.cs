using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Forwarding.Converters;

/// <summary>
/// 请求转换器：OpenAI Chat Completions 格式 → Anthropic Messages 格式。
/// 使用 <see cref="JsonNode"/> 做树级改写。fail-open：解析失败或结构不符时原样返回。
/// 字段映射要点：
/// - 首条 role:system 消息 → system 字段（字符串）
/// - content 字符串 → [{type:text,text}]；image_url→image block；tool_calls→tool_use block
/// - role:tool 消息 → role:user + {type:tool_result,tool_use_id,content}
/// - tools：{type:function,function:{name,description,parameters}}→{name,description,input_schema}
/// - tool_choice 反向映射；stop→stop_sequences；max_tokens 缺失补 4096（Anthropic 必填）
/// - 丢弃 n/presence_penalty/frequency_penalty/logit_bias/user
/// </summary>
public sealed class OpenAiToAnthropicRequestConverter : IRequestConverter
{
    private const int DefaultMaxTokens = 4096; // Anthropic 必填 max_tokens，OpenAI 缺失时补默认值

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
            return requestBody;
        }
        if (root is not JsonObject openai)
        {
            return requestBody;
        }

        try
        {
            var anthropic = new JsonObject();

            CopyIfPresent(openai, anthropic, "model");
            CopyIfPresent(openai, anthropic, "temperature");
            CopyIfPresent(openai, anthropic, "top_p");
            CopyIfPresent(openai, anthropic, "stream");

            // max_tokens：Anthropic 必填，缺失补默认
            if (openai.TryGetPropertyValue("max_tokens", out var mt) && mt is not null)
            {
                anthropic["max_tokens"] = JsonNode.Parse(mt.ToJsonString());
            }
            else
            {
                anthropic["max_tokens"] = DefaultMaxTokens;
            }

            // stop → stop_sequences
            if (openai.TryGetPropertyValue("stop", out var stop) && stop is not null)
            {
                anthropic["stop_sequences"] = NormalizeToStringArray(stop);
            }

            // messages 转换：首条 system 提取为 system 字段，其余转换
            string? systemText = null;
            var anthropicMessages = new JsonArray();

            if (openai.TryGetPropertyValue("messages", out var msgs) && msgs is JsonArray messages)
            {
                bool systemExtracted = false;
                foreach (var msg in messages)
                {
                    if (msg is not JsonObject m) continue;
                    var role = m["role"]?.GetValue<string>();

                    // 仅提取首条 system 消息为 system 字段（后续 system 消息保留为 user 消息）
                    if (!systemExtracted && role == "system")
                    {
                        systemText = ExtractMessageText(m);
                        systemExtracted = true;
                        continue;
                    }

                    anthropicMessages.Add(ConvertMessage(m));
                }
            }
            anthropic["messages"] = anthropicMessages;

            if (!string.IsNullOrEmpty(systemText))
            {
                anthropic["system"] = systemText;
            }

            // tools 转换
            if (openai.TryGetPropertyValue("tools", out var tools) && tools is JsonArray toolsArr)
            {
                var anthropicTools = new JsonArray();
                foreach (var t in toolsArr)
                {
                    if (t is not JsonObject tool) continue;
                    // 兼容两种形态：{type:function,function:{...}} 或直接 {...}
                    JsonObject fn;
                    if (tool.TryGetPropertyValue("function", out var fnNode) && fnNode is JsonObject fnObj)
                    {
                        fn = fnObj;
                    }
                    else
                    {
                        fn = tool;
                    }
                    var at = new JsonObject
                    {
                        ["name"] = fn["name"]?.DeepClone(),
                        ["description"] = fn["description"]?.DeepClone()
                    };
                    if (fn.TryGetPropertyValue("parameters", out var p) && p is not null)
                    {
                        at["input_schema"] = JsonNode.Parse(p.ToJsonString());
                    }
                    anthropicTools.Add(at);
                }
                anthropic["tools"] = anthropicTools;
            }

            // tool_choice 转换
            if (openai.TryGetPropertyValue("tool_choice", out var tc) && tc is not null)
            {
                var converted = ConvertToolChoiceToAnthropic(tc);
                if (converted is not null)
                {
                    anthropic["tool_choice"] = converted;
                }
            }

            return anthropic.ToJsonString();
        }
        catch
        {
            return requestBody;
        }
    }

    /// <summary>转换单条 OpenAI 消息为 Anthropic 消息</summary>
    private static JsonObject ConvertMessage(JsonObject m)
    {
        var role = m["role"]?.GetValue<string>() ?? "user";
        var anthropic = new JsonObject();

        // role:tool → role:user + tool_result block
        if (role == "tool")
        {
            var toolCallId = m["tool_call_id"]?.GetValue<string>() ?? "";
            var content = ExtractMessageText(m);
            anthropic["role"] = "user";
            anthropic["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolCallId,
                    ["content"] = content
                }
            };
            return anthropic;
        }

        anthropic["role"] = role == "assistant" ? "assistant" : "user";

        var contentBlocks = new JsonArray();
        bool hasContent = false;

        // content（字符串或数组）
        if (m.TryGetPropertyValue("content", out var c) && c is not null)
        {
            if (c is JsonValue cv && cv.TryGetValue<string>(out var s))
            {
                if (!string.IsNullOrEmpty(s))
                {
                    contentBlocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = s
                    });
                    hasContent = true;
                }
            }
            else if (c is JsonArray arr)
            {
                foreach (var b in arr)
                {
                    if (b is not JsonObject block) continue;
                    var type = block["type"]?.GetValue<string>();
                    switch (type)
                    {
                        case "text":
                            contentBlocks.Add(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = block["text"]?.DeepClone()
                            });
                            hasContent = true;
                            break;
                        case "image_url":
                            {
                                // OpenAI {type:image_url, image_url:{url:"data:{media};base64,{data}"}}
                                // → Anthropic {type:image, source:{type:base64, media_type, data}}
                                var url = block["image_url"]?["url"]?.GetValue<string>() ?? "";
                                var (mediaType, data) = ParseDataUrl(url);
                                if (data is not null)
                                {
                                    contentBlocks.Add(new JsonObject
                                    {
                                        ["type"] = "image",
                                        ["source"] = new JsonObject
                                        {
                                            ["type"] = "base64",
                                            ["media_type"] = mediaType,
                                            ["data"] = data
                                        }
                                    });
                                    hasContent = true;
                                }
                                break;
                            }
                    }
                }
            }
        }

        // tool_calls → tool_use blocks（assistant 消息）
        if (m.TryGetPropertyValue("tool_calls", out var tc) && tc is JsonArray tcArr)
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
                hasContent = true;
            }
        }

        // Anthropic 要求 content 非空（除非是 tool_use/tool_result 场景）。兜底空文本
        if (!hasContent)
        {
            contentBlocks.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = ""
            });
        }

        anthropic["content"] = contentBlocks;
        return anthropic;
    }

    /// <summary>提取 OpenAI 消息的纯文本 content（用于 system / tool 消息）</summary>
    private static string ExtractMessageText(JsonObject m)
    {
        if (!m.TryGetPropertyValue("content", out var c) || c is null)
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

    private static JsonNode? ConvertToolChoiceToAnthropic(JsonNode tc)
    {
        if (tc is JsonValue sv && sv.TryGetValue<string>(out var s))
        {
            return s switch
            {
                "auto" => new JsonObject { ["type"] = "auto" },
                "none" => new JsonObject { ["type"] = "none" },
                "required" => new JsonObject { ["type"] = "any" },
                _ => new JsonObject { ["type"] = "auto" }
            };
        }
        if (tc is JsonObject obj)
        {
            var type = obj["type"]?.GetValue<string>();
            if (type == "function")
            {
                var name = obj["function"]?["name"]?.GetValue<string>() ?? "";
                return new JsonObject
                {
                    ["type"] = "tool",
                    ["name"] = name
                };
            }
        }
        return null;
    }

    /// <summary>解析 data URL（data:{media};base64,{data}）为 (mediaType, base64Data)</summary>
    private static (string mediaType, string? data) ParseDataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ("image/png", null);
        }
        var rest = url[5..];
        var comma = rest.IndexOf(',');
        if (comma < 0)
        {
            return ("image/png", null);
        }
        var meta = rest[..comma];
        var data = rest[(comma + 1)..];
        var mediaType = "image/png";
        const string b64Marker = ";base64";
        if (meta.EndsWith(b64Marker, StringComparison.OrdinalIgnoreCase))
        {
            mediaType = meta[..^b64Marker.Length];
            if (string.IsNullOrEmpty(mediaType)) mediaType = "image/png";
        }
        return (mediaType, data);
    }

    /// <summary>将 stop 字段（字符串或数组）归一化为 JSON 字符串数组</summary>
    private static JsonNode NormalizeToStringArray(JsonNode stop)
    {
        if (stop is JsonValue sv && sv.TryGetValue<string>(out var s))
        {
            return new JsonArray { JsonValue.Create(s) };
        }
        return JsonNode.Parse(stop.ToJsonString())!;
    }

    private static void CopyIfPresent(JsonObject src, JsonObject dst, string key)
    {
        if (src.TryGetPropertyValue(key, out var v) && v is not null)
        {
            dst[key] = JsonNode.Parse(v.ToJsonString());
        }
    }
}
