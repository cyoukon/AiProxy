using AiProxy.Config;
using AiProxy.Forwarding.Converters;
using Microsoft.AspNetCore.Http;

namespace AiProxy.Tests;

public class EndpointPathMapperTests
{
    [Fact]
    public void Map_AnthropicToOpenAi_RewritesMessages()
    {
        var result = EndpointPathMapper.Map(new PathString("/v1/messages"), ServiceFormat.Anthropic, ServiceFormat.OpenAI);
        Assert.Equal(new PathString("/v1/chat/completions"), result);
    }

    [Fact]
    public void Map_OpenAiToAnthropic_RewritesChatCompletions()
    {
        var result = EndpointPathMapper.Map(new PathString("/v1/chat/completions"), ServiceFormat.OpenAI, ServiceFormat.Anthropic);
        Assert.Equal(new PathString("/v1/messages"), result);
    }

    [Fact]
    public void Map_SameFormat_PassThrough()
    {
        var result = EndpointPathMapper.Map(new PathString("/v1/chat/completions"), ServiceFormat.OpenAI, ServiceFormat.OpenAI);
        Assert.Equal(new PathString("/v1/chat/completions"), result);
    }

    [Fact]
    public void Map_NoPrefix_RewritesMessages()
    {
        var result = EndpointPathMapper.Map(new PathString("/messages"), ServiceFormat.Anthropic, ServiceFormat.OpenAI);
        Assert.Equal(new PathString("/chat/completions"), result);
    }

    [Fact]
    public void Map_UnknownPath_PassThrough()
    {
        var result = EndpointPathMapper.Map(new PathString("/v1/unknown"), ServiceFormat.Anthropic, ServiceFormat.OpenAI);
        Assert.Equal(new PathString("/v1/unknown"), result);
    }
}
