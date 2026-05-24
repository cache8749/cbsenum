// Translation of WildcardMatching.pas
// '?' matches any single character except '.'
// '*' matches zero or more characters

namespace CBSEnum;

public static class WildcardMatching
{
    /// <summary>Case-sensitive wildcard match. Caller must pre-lowercase both arguments for case-insensitive use.</summary>
    public static bool Match(string text, string pattern)
        => MatchAt(text, 0, pattern, 0);

    private static bool MatchAt(string text, int ti, string pattern, int pi)
    {
        while (pi < pattern.Length)
        {
            char p = pattern[pi];
            if (p == '*')
            {
                // Consume all consecutive stars
                while (pi < pattern.Length && pattern[pi] == '*') pi++;
                if (pi == pattern.Length) return true;   // trailing * matches anything
                // Try matching the rest of the pattern at each position in text
                while (ti <= text.Length)
                {
                    if (MatchAt(text, ti, pattern, pi)) return true;
                    ti++;
                }
                return false;
            }
            else if (ti >= text.Length)
            {
                return false;
            }
            else if (p == '?' && text[ti] != '.')
            {
                ti++; pi++;
            }
            else if (p == text[ti])
            {
                ti++; pi++;
            }
            else
            {
                return false;
            }
        }
        return ti == text.Length;
    }
}
