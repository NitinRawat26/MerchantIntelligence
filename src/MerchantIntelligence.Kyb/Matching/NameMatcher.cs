using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MerchantIntelligence.Kyb.Matching;

/// <summary>
/// Fuzzy matching for legal entity and person names. Combines a token-set overlap score
/// with Jaro-Winkler on the normalised strings so both re-ordered words
/// ("Smith, John" vs "John Smith") and typos ("Acme Holdngs") score well.
/// </summary>
public static class NameMatcher
{
    private static readonly Regex NonAlnum = new(@"[^a-z0-9 ]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.Ordinal)
    {
        "inc", "incorporated", "corp", "corporation", "co", "company", "ltd", "limited", "llc", "lc",
        "llp", "lp", "plc", "gmbh", "ag", "sa", "sarl", "srl", "bv", "nv", "pty", "pte", "oy", "ab", "as",
        "kk", "the", "and", "of", "holdings", "holding", "group", "international", "intl", "enterprises",
        "trading", "services", "solutions", "technologies", "technology", "dba", "sp", "zoo", "spa", "ltda"
    };

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        var lower = sb.ToString().ToLowerInvariant().Replace('&', ' ').Replace("+", " and ");
        lower = NonAlnum.Replace(lower, " ");
        return Whitespace.Replace(lower, " ").Trim();
    }

    public static IReadOnlyList<string> Tokens(string? name, bool stripLegalSuffixes = true)
    {
        var tokens = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!stripLegalSuffixes) return tokens;
        var stripped = tokens.Where(t => !LegalSuffixes.Contains(t)).ToArray();
        return stripped.Length > 0 ? stripped : tokens;
    }

    /// <summary>Overall similarity in [0, 1].</summary>
    public static double Similarity(string? a, string? b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;

        var tokenSet = TokenSetRatio(ta, tb);
        var jw = JaroWinkler(string.Join(' ', ta), string.Join(' ', tb));
        var sorted = JaroWinkler(string.Join(' ', ta.OrderBy(t => t, StringComparer.Ordinal)),
                                 string.Join(' ', tb.OrderBy(t => t, StringComparer.Ordinal)));
        // Whole-string Jaro-Winkler rewards a shared leading token far too much ("Viktor Bout" vs "Viktor Ignatov"),
        // so it only contributes as typo tolerance blended with token overlap.
        var best = Math.Max(jw, sorted);
        return Math.Round(Math.Max(tokenSet, 0.5 * tokenSet + 0.5 * best), 4);
    }

    /// <summary>Fraction of tokens shared, allowing fuzzy token equality (JW ≥ 0.9) and initials.</summary>
    public static double TokenSetRatio(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var used = new bool[b.Count];
        var matched = 0;
        foreach (var t in a)
        {
            for (var i = 0; i < b.Count; i++)
            {
                if (used[i]) continue;
                if (TokensEquivalent(t, b[i]))
                {
                    used[i] = true;
                    matched++;
                    break;
                }
            }
        }
        // Weight against the shorter list so "Acme" matches "Acme Holdings LLC", but penalise
        // large length differences slightly so a single common token doesn't give a perfect score.
        var shorter = Math.Min(a.Count, b.Count);
        var longer = Math.Max(a.Count, b.Count);
        var coverage = (double)matched / shorter;
        var lengthPenalty = 1 - 0.1 * Math.Min(3, longer - shorter);
        return coverage * lengthPenalty;
    }

    private static bool TokensEquivalent(string x, string y)
    {
        if (x == y) return true;
        if (x.Length == 1 && y.StartsWith(x, StringComparison.Ordinal)) return true;
        if (y.Length == 1 && x.StartsWith(y, StringComparison.Ordinal)) return true;
        if (x.Length < 4 || y.Length < 4) return false;
        var threshold = Math.Min(x.Length, y.Length) >= 5 ? 0.85 : 0.9;
        return JaroWinkler(x, y) >= threshold;
    }

    public static double JaroWinkler(string s1, string s2)
    {
        if (s1.Length == 0 && s2.Length == 0) return 1;
        if (s1.Length == 0 || s2.Length == 0) return 0;
        if (s1 == s2) return 1;

        var matchDistance = Math.Max(s1.Length, s2.Length) / 2 - 1;
        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];
        var matches = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, s2.Length);
            for (var j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = s2Matches[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0;

        var transpositions = 0;
        var k = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        var m = (double)matches;
        var jaro = (m / s1.Length + m / s2.Length + (m - transpositions / 2.0) / m) / 3;
        var prefix = 0;
        for (var i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)); i++)
        {
            if (s1[i] == s2[i]) prefix++; else break;
        }
        return jaro + prefix * 0.1 * (1 - jaro);
    }
}
