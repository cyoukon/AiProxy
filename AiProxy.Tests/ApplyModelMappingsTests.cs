using System.Text.Json.Nodes;
using AiProxy.Config;
using AiProxy.Forwarding;

namespace AiProxy.Tests;

public class ApplyModelMappingsTests
{
    private static AiServiceOptions MakeService(params ModelMappingOptions[] mappings)
    {
        return new AiServiceOptions { ModelMappings = mappings.ToList() };
    }

    [Fact]
    public void EmptyMappings_NoChange()
    {
        var body = """{"model":"gpt-4"}""";
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, MakeService());
        Assert.False(changed);
        Assert.Equal(body, result);
    }

    [Fact]
    public void ExactMatch_Replaces()
    {
        var body = """{"model":"gpt-4"}""";
        var svc = MakeService(new ModelMappingOptions { Pattern = "gpt-4", Replacement = "gpt-4o", Enabled = true });
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, svc);
        Assert.True(changed);
        Assert.Equal("gpt-4o", JsonNode.Parse(result)?["model"]?.ToString());
    }

    [Fact]
    public void WildcardMatch_Replaces()
    {
        var body = """{"model":"gpt-4o"}""";
        var svc = MakeService(new ModelMappingOptions { Pattern = "gpt-4*", Replacement = "gpt-4-turbo", Enabled = true });
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, svc);
        Assert.True(changed);
        Assert.Equal("gpt-4-turbo", JsonNode.Parse(result)?["model"]?.ToString());
    }

    [Fact]
    public void FirstMatchWins()
    {
        var body = """{"model":"gpt-4"}""";
        var svc = MakeService(
            new ModelMappingOptions { Pattern = "gpt-4", Replacement = "a", Enabled = true },
            new ModelMappingOptions { Pattern = "gpt-4", Replacement = "b", Enabled = true });
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, svc);
        Assert.True(changed);
        Assert.Equal("a", JsonNode.Parse(result)?["model"]?.ToString());
    }

    [Fact]
    public void DisabledMapping_Skipped()
    {
        var body = """{"model":"gpt-4"}""";
        var svc = MakeService(new ModelMappingOptions { Pattern = "gpt-4", Replacement = "a", Enabled = false });
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, svc);
        Assert.False(changed);
    }

    [Fact]
    public void NoMatch_NoChange()
    {
        var body = """{"model":"gpt-4"}""";
        var svc = MakeService(new ModelMappingOptions { Pattern = "claude*", Replacement = "a", Enabled = true });
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, svc);
        Assert.False(changed);
    }

    [Fact]
    public void SameReplacement_NoChange()
    {
        var body = """{"model":"gpt-4"}""";
        var svc = MakeService(new ModelMappingOptions { Pattern = "gpt-4", Replacement = "gpt-4", Enabled = true });
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, svc);
        Assert.False(changed);
    }

    [Fact]
    public void NoModelField_NoChange()
    {
        var body = """{"messages":[]}""";
        var svc = MakeService(new ModelMappingOptions { Pattern = "*", Replacement = "x", Enabled = true });
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, svc);
        Assert.False(changed);
    }

    [Fact]
    public void CaseInsensitive_Matches()
    {
        var body = """{"model":"GPT-4"}""";
        var svc = MakeService(new ModelMappingOptions { Pattern = "gpt-4", Replacement = "gpt-4o", Enabled = true, CaseSensitive = false });
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, svc);
        Assert.True(changed);
        Assert.Equal("gpt-4o", JsonNode.Parse(result)?["model"]?.ToString());
    }

    [Fact]
    public void CaseSensitive_NoMatch()
    {
        var body = """{"model":"GPT-4"}""";
        var svc = MakeService(new ModelMappingOptions { Pattern = "gpt-4", Replacement = "gpt-4o", Enabled = true, CaseSensitive = true });
        var (result, changed) = ForwardingEndpoint.ApplyModelMappings(body, svc);
        Assert.False(changed);
    }
}
