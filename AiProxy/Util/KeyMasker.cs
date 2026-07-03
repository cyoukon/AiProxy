namespace AiProxy.Util;

/// <summary>
/// 密钥脱敏工具：仅展示末尾 4 位，其余替换为 ****。
/// 形如 "sk-abcd1234efgh5678" -> "sk-****5678"。
/// 短密钥（&lt;=4 位）整体替换为 ****。
/// </summary>
public static class KeyMasker
{
    private const string MaskedPlaceholder = "****";

    /// <summary>对任意 ApiKey 进行脱敏，安全用于控制台、日志、管理面板输出</summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        // 长度 <= 4，直接全部脱敏，不泄露任何字符
        if (key.Length <= 4)
        {
            return MaskedPlaceholder;
        }

        // 保留前缀（如 "sk-"）+ 脱敏 + 末尾 4 位
        // 简单稳健：前 3 位 + **** + 后 4 位（如果前缀不足 3 位则全部用 ****）
        int tailLen = 4;
        int prefixLen = Math.Min(3, key.Length - tailLen);
        if (prefixLen <= 0)
        {
            return MaskedPlaceholder + key[^tailLen..];
        }

        return string.Concat(key.AsSpan(0, prefixLen), MaskedPlaceholder, key.AsSpan(key.Length - tailLen));
    }
}
