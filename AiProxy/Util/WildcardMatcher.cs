namespace AiProxy.Util;

/// <summary>
/// 通配符匹配工具。
/// 通配符规则：<c>*</c> 匹配任意数量字符（含空），<c>?</c> 匹配单个字符，其余字符原义匹配。
/// 模式始终锚定全串，不会子串命中。使用双指针回溯算法，零分配。
/// </summary>
public static class WildcardMatcher
{
    /// <summary>
    /// 判断 <paramref name="input"/> 是否匹配通配符模式 <paramref name="wildcardPattern"/>。
    /// 默认区分大小写；<paramref name="ignoreCase"/> 为 true 时忽略大小写。
    /// </summary>
    public static bool IsMatch(string input, string wildcardPattern, bool ignoreCase = false)
    {
        int pi = 0, ii = 0;
        int starPi = -1, starIi = -1;

        while (ii < input.Length)
        {
            if (pi < wildcardPattern.Length)
            {
                var pc = wildcardPattern[pi];
                if (pc == '?')
                {
                    pi++;
                    ii++;
                    continue;
                }
                if (pc == '*')
                {
                    starPi = pi;
                    starIi = ii;
                    pi++;
                    continue;
                }
                // literal comparison
                var ic = input[ii];
                if (ignoreCase ? char.ToLowerInvariant(pc) == char.ToLowerInvariant(ic) : pc == ic)
                {
                    pi++;
                    ii++;
                    continue;
                }
            }

            // 不匹配：回溯到上一个 * 的位置，让 * 多吃一个字符
            if (starPi >= 0)
            {
                pi = starPi + 1;
                starIi++;
                ii = starIi;
                continue;
            }

            return false;
        }

        // 剩余 pattern 必须全是 * 才算匹配
        while (pi < wildcardPattern.Length && wildcardPattern[pi] == '*')
        {
            pi++;
        }

        return pi == wildcardPattern.Length;
    }
}
