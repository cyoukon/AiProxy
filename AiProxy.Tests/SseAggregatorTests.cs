using System.Text;
using System.Text.Json;
using AiProxy.Forwarding;

namespace AiProxy.Tests;

public class SseAggregatorTests
{
    [Fact]
    public void Aggregate_EmptyBytes_ReturnsEmpty()
    {
        var (content, p, c, t) = SseAggregator.Aggregate(ReadOnlySpan<byte>.Empty);
        Assert.Equal(string.Empty, content);
        Assert.Null(p);
        Assert.Null(c);
        Assert.Null(t);
    }

    [Fact]
    public void Aggregate_OpenAiStream_ReconstructsFullResponse()
    {
        var sse = "data: {\"id\":\"chatcmpl-1\",\"model\":\"gpt-4\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"chatcmpl-1\",\"model\":\"gpt-4\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" world\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var bytes = Encoding.UTF8.GetBytes(sse);

        var (content, p, c, t) = SseAggregator.Aggregate(bytes);

        Assert.NotEmpty(content);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        // Content should combine both deltas
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        Assert.Equal("Hello world", text);
        Assert.Equal("stop", doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void Aggregate_AnthropicStream_ReconstructsFullResponse()
    {
        var sse = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-1\",\"model\":\"claude-3\",\"usage\":{\"input_tokens\":10}}}\n\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":5}}\n\n";
        var bytes = Encoding.UTF8.GetBytes(sse);

        var (content, p, c, t) = SseAggregator.Aggregate(bytes);

        Assert.NotEmpty(content);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal("message", doc.RootElement.GetProperty("type").GetString());
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal("Hi", text);
        Assert.Equal(10, p);
        Assert.Equal(5, c);
        Assert.Equal(15, t);
    }

    [Fact]
    public void Aggregate_NonJsonFallback_ReturnsRawPayloads()
    {
        var sse = "data: not-json-here\n\n";
        var bytes = Encoding.UTF8.GetBytes(sse);

        var (content, p, c, t) = SseAggregator.Aggregate(bytes);

        Assert.Contains("not-json-here", content);
        Assert.Null(p);
        Assert.Null(c);
        Assert.Null(t);
    }
}
