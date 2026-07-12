using AiProxy.Util;

namespace AiProxy.Tests;

public class KeyMaskerTests
{
    [Fact]
    public void Mask_NormalLongKey_MasksMiddle()
    {
        Assert.Equal("sk-****5678", KeyMasker.Mask("sk-abcd1234efgh5678"));
    }

    [Fact]
    public void Mask_ShortKey_FullyMasked()
    {
        Assert.Equal("****", KeyMasker.Mask("abc"));
    }

    [Fact]
    public void Mask_Exactly4Chars_FullyMasked()
    {
        Assert.Equal("****", KeyMasker.Mask("abcd"));
    }

    [Fact]
    public void Mask_5Chars_PrefixAndTail()
    {
        // prefixLen = min(3, 5-4) = 1, so: 1 char + **** + 4 chars
        Assert.Equal("a****bcde", KeyMasker.Mask("abcde"));
    }

    [Fact]
    public void Mask_6Chars_PrefixAndTail()
    {
        // prefixLen = min(3, 6-4) = 2, so: 2 chars + **** + 4 chars
        Assert.Equal("ab****cdef", KeyMasker.Mask("abcdef"));
    }

    [Fact]
    public void Mask_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, KeyMasker.Mask(null));
    }

    [Fact]
    public void Mask_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, KeyMasker.Mask(""));
    }
}
