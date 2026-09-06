using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.Kyb.Prohibited;

public enum BusinessPolicy
{
    /// <summary>No restricted category detected.</summary>
    Acceptable,
    /// <summary>Acceptable with enhanced due diligence / registration.</summary>
    HighRisk,
    /// <summary>Requires licensing evidence, dedicated MCC or card-brand registration.</summary>
    Restricted,
    /// <summary>Not accepted.</summary>
    Prohibited
}

public sealed record RestrictedCategory(
    string Code,
    string Name,
    BusinessPolicy Policy,
    IReadOnlyList<int> Mccs,
    IReadOnlyList<string> Keywords,
    string Notes);

public sealed record CategoryMatch(
    RestrictedCategory Category,
    double Score,
    IReadOnlyList<string> MatchedKeywords,
    bool DeclaredMccInCategory);

public sealed record ProhibitedBusinessResult(
    BusinessPolicy Verdict,
    IReadOnlyList<CategoryMatch> Matches,
    IReadOnlyList<KybProhibitedFlag> Flags);

public sealed record KybProhibitedFlag(string Code, string Message, RiskTier Severity);

/// <summary>
/// Keyword taxonomy for card-brand prohibited / restricted / high-brand-risk business types.
/// Scores the merchant's website text and stated business description; the declared MCC alone
/// is never enough to trigger a match but it raises the score for a keyword-matched category.
/// </summary>
public sealed class ProhibitedBusinessDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IReadOnlyList<(RestrictedCategory Category, Regex[] Patterns)> _compiled;

    public ProhibitedBusinessDetector(IEnumerable<RestrictedCategory> categories)
    {
        _compiled = categories.Select(c => (c, c.Keywords
            .Select(k => new Regex($@"(?<![a-z0-9]){Regex.Escape(k.ToLowerInvariant())}(?![a-z0-9])", RegexOptions.Compiled))
            .ToArray())).ToList();
    }

    public static ProhibitedBusinessDetector Default { get; } = LoadEmbedded();

    public IReadOnlyList<RestrictedCategory> Categories => _compiled.Select(c => c.Category).ToList();

    public ProhibitedBusinessResult Analyze(string? websiteText, string? businessDescription = null, int? declaredMcc = null)
    {
        var text = ((websiteText ?? string.Empty) + " " + (businessDescription ?? string.Empty)).ToLowerInvariant();
        var description = (businessDescription ?? string.Empty).ToLowerInvariant();
        var matches = new List<CategoryMatch>();

        foreach (var (category, patterns) in _compiled)
        {
            var matched = new List<string>();
            double score = 0;
            for (var i = 0; i < patterns.Length; i++)
            {
                var count = patterns[i].Matches(text).Count;
                if (count == 0) continue;
                var keyword = category.Keywords[i];
                matched.Add(keyword);
                // Specific multi-word phrases are stronger evidence; the self-declared description is strongest.
                var weight = keyword.Contains(' ') ? 1.5 : 1.0;
                if (patterns[i].IsMatch(description)) weight *= 2;
                score += weight * Math.Log(1 + count);
            }
            var mccHit = declaredMcc is int mcc && category.Mccs.Contains(mcc);
            if (matched.Count == 0) continue;
            if (mccHit) score *= 1.5;

            // Distinct keyword breadth matters more than repetition of one term.
            var normalised = Math.Min(1.0, (score * (0.5 + 0.5 * Math.Min(matched.Count, 4) / 4.0)) / 4.0);
            matches.Add(new CategoryMatch(category, Math.Round(normalised, 3), matched, mccHit));
        }

        matches = matches.Where(m => m.Score >= 0.15 || (m.DeclaredMccInCategory && m.Score > 0))
            .OrderByDescending(m => m.Score).ToList();

        var flags = new List<KybProhibitedFlag>();
        var verdict = BusinessPolicy.Acceptable;
        foreach (var m in matches.Where(m => m.Score >= 0.35 || m.DeclaredMccInCategory))
        {
            var severity = m.Category.Policy == BusinessPolicy.Prohibited ? RiskTier.High
                : m.Category.Policy == BusinessPolicy.Restricted ? RiskTier.High : RiskTier.Medium;
            flags.Add(new KybProhibitedFlag($"{m.Category.Policy.ToString().ToUpperInvariant()}_{m.Category.Code}",
                $"{m.Category.Name} ({m.Category.Policy}): matched {string.Join(", ", m.MatchedKeywords.Take(5))}{(m.DeclaredMccInCategory ? "; declared MCC is in this category" : string.Empty)}. {m.Category.Notes}",
                severity));
            if (m.Category.Policy > verdict) verdict = m.Category.Policy;
        }

        return new ProhibitedBusinessResult(verdict, matches, flags);
    }

    private static ProhibitedBusinessDetector LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("restricted-categories.json", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        var categories = JsonSerializer.Deserialize<List<RestrictedCategory>>(stream, JsonOptions)
                         ?? throw new InvalidOperationException("restricted-categories.json is empty.");
        return new ProhibitedBusinessDetector(categories);
    }
}
