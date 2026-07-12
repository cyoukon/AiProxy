using AiProxy.Util;

namespace AiProxy.Tests;

public class WildcardMatcherTests
{
    [Theory]
    [InlineData("gpt-4", "gpt-4", true)]
    [InlineData("gpt-4", "gpt-4o", false)]
    [InlineData("gpt-4*", "gpt-4o", true)]
    [InlineData("gpt-4*", "gpt-4", true)]
    [InlineData("gpt-4*", "gpt-4-turbo-preview", true)]
    [InlineData("gpt-?", "gpt-4", true)]
    [InlineData("gpt-?", "gpt-", false)]
    [InlineData("gpt-?", "gpt-4o", false)]
    [InlineData("*-*", "claude-3-opus", true)]
    [InlineData("", "gpt-4", false)]
    [InlineData("gpt-4", "", false)]
    [InlineData("", "", true)]
    [InlineData("*", "anything", true)]
    [InlineData("GPT-4", "gpt-4", false)]
    [InlineData("gpt*4", "gpt-4", true)]
    [InlineData("g?ting", "gating", true)]
    [InlineData("g?ting", "getting", false)]
    [InlineData("??-4", "gp-4", true)]
    public void IsMatch_ReturnsExpected(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, WildcardMatcher.IsMatch(input, pattern));
    }

    [Theory]
    [InlineData("GPT-4", "gpt-4", true)]
    [InlineData("gpt-4*", "GPT-4O", true)]
    [InlineData("GPT-4", "gpt-4o", false)]
    [InlineData("Claude*", "claude-3-opus", true)]
    public void IsMatch_IgnoreCase_ReturnsExpected(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, WildcardMatcher.IsMatch(input, pattern, ignoreCase: true));
    }
}
