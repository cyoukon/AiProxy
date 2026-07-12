using AiProxy.Forwarding;

namespace AiProxy.Tests;

public class OpenAiParserTests
{
    // ─── TryGetModel ────────────────────────────────────────────────────────

    [Fact]
    public void TryGetModel_NormalJson_ReturnsModel()
    {
        Assert.Equal("gpt-4", OpenAiParser.TryGetModel("""{"model":"gpt-4","messages":[]}"""));
    }

    [Fact]
    public void TryGetModel_NoModel_ReturnsNull()
    {
        Assert.Null(OpenAiParser.TryGetModel("""{"messages":[]}"""));
    }

    [Fact]
    public void TryGetModel_ModelNotString_ReturnsNull()
    {
        Assert.Null(OpenAiParser.TryGetModel("""{"model":123}"""));
    }

    [Fact]
    public void TryGetModel_EmptyJson_ReturnsNull()
    {
        Assert.Null(OpenAiParser.TryGetModel("{}"));
    }

    [Fact]
    public void TryGetModel_Null_ReturnsNull()
    {
        Assert.Null(OpenAiParser.TryGetModel(null));
    }

    [Fact]
    public void TryGetModel_EmptyString_ReturnsNull()
    {
        Assert.Null(OpenAiParser.TryGetModel(""));
    }

    [Fact]
    public void TryGetModel_NotJson_ReturnsNull()
    {
        Assert.Null(OpenAiParser.TryGetModel("not json"));
    }

    // ─── TryGetUsage ────────────────────────────────────────────────────────

    [Fact]
    public void TryGetUsage_OpenAiFormat_ReturnsAllTokens()
    {
        var (p, c, t) = OpenAiParser.TryGetUsage(
            """{"usage":{"prompt_tokens":10,"completion_tokens":20,"total_tokens":30}}""");
        Assert.Equal(10, p);
        Assert.Equal(20, c);
        Assert.Equal(30, t);
    }

    [Fact]
    public void TryGetUsage_AnthropicFormat_ComputesTotal()
    {
        var (p, c, t) = OpenAiParser.TryGetUsage(
            """{"usage":{"input_tokens":10,"output_tokens":20}}""");
        Assert.Equal(10, p);
        Assert.Equal(20, c);
        Assert.Equal(30, t);
    }

    [Fact]
    public void TryGetUsage_NoUsage_ReturnsAllNull()
    {
        var (p, c, t) = OpenAiParser.TryGetUsage("{}");
        Assert.Null(p);
        Assert.Null(c);
        Assert.Null(t);
    }

    [Fact]
    public void TryGetUsage_Null_ReturnsAllNull()
    {
        var (p, c, t) = OpenAiParser.TryGetUsage(null);
        Assert.Null(p);
        Assert.Null(c);
        Assert.Null(t);
    }
}
