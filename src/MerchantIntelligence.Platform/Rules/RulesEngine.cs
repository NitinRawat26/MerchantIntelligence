using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Scoring;

namespace MerchantIntelligence.Platform.Rules;

public enum RuleOutcome
{
    Approve,
    Refer,
    Decline
}

/// <summary>
/// A condition tree: either a leaf (<c>fact op value</c>) or an <c>all</c>/<c>any</c>/<c>not</c> combinator.
/// Facts are looked up case-insensitively in the fact bag built by <see cref="RulesEngine.BuildFacts"/>.
/// </summary>
public sealed class RuleCondition
{
    public string? Fact { get; set; }
    public string? Op { get; set; }
    public JsonElement? Value { get; set; }
    public List<RuleCondition>? All { get; set; }
    public List<RuleCondition>? Any { get; set; }
    public RuleCondition? Not { get; set; }
}

public sealed class Rule
{
    public string Id { get; set; } = string.Empty;
    public int Priority { get; set; } = 50;
    public RuleOutcome Outcome { get; set; } = RuleOutcome.Refer;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public RuleCondition When { get; set; } = new();
}

public sealed class RuleSet
{
    public string Version { get; set; } = "1.0";
    public string? Description { get; set; }
    public RuleOutcome DefaultOutcome { get; set; } = RuleOutcome.Refer;
    public List<Rule> Rules { get; set; } = new();
}

public sealed record RuleHit(string Id, RuleOutcome Outcome, int Priority, string? Description);

public sealed record RulesEvaluation(
    RuleOutcome Outcome,
    IReadOnlyList<RuleHit> MatchedRules,
    string DecidingRule,
    string RuleSetVersion,
    IReadOnlyDictionary<string, object?> Facts);

public sealed class RuleValidationException(string message) : Exception(message);

/// <summary>
/// Data-driven policy layer that sits on top of the ML/heuristic scores. Rules are plain JSON so risk
/// teams can change thresholds without a deploy. The most severe outcome among matched rules wins
/// (Decline &gt; Refer &gt; Approve); the lowest-priority-number rule with that outcome is reported as deciding.
/// </summary>
public sealed class RulesEngine
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private static readonly HashSet<string> Ops = ["eq", "neq", "gt", "gte", "lt", "lte", "contains", "notcontains", "in", "exists"];

    public static RuleSet LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MerchantIntelligence.Platform.Resources.default-rules.json")
            ?? throw new InvalidOperationException("default-rules.json missing");
        return JsonSerializer.Deserialize<RuleSet>(stream, JsonOptions) ?? new RuleSet();
    }

    public static RuleSet Parse(string json)
    {
        RuleSet set;
        try
        {
            set = JsonSerializer.Deserialize<RuleSet>(json, JsonOptions) ?? throw new RuleValidationException("Rule set is empty.");
        }
        catch (JsonException ex)
        {
            throw new RuleValidationException($"Invalid rule JSON: {ex.Message}");
        }
        Validate(set);
        return set;
    }

    public static void Validate(RuleSet set)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in set.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) throw new RuleValidationException("Every rule needs an id.");
            if (!ids.Add(r.Id)) throw new RuleValidationException($"Duplicate rule id '{r.Id}'.");
            ValidateCondition(r.When, r.Id);
        }
    }

    private static void ValidateCondition(RuleCondition c, string ruleId)
    {
        var combinators = (c.All is not null ? 1 : 0) + (c.Any is not null ? 1 : 0) + (c.Not is not null ? 1 : 0);
        if (combinators > 1) throw new RuleValidationException($"Rule '{ruleId}': use only one of all/any/not per node.");
        if (combinators == 1)
        {
            if (c.Fact is not null) throw new RuleValidationException($"Rule '{ruleId}': a node cannot have both a fact and a combinator.");
            foreach (var child in c.All ?? c.Any ?? [c.Not!]) ValidateCondition(child, ruleId);
            if ((c.All ?? c.Any)?.Count == 0) throw new RuleValidationException($"Rule '{ruleId}': empty all/any.");
            return;
        }
        if (string.IsNullOrWhiteSpace(c.Fact)) throw new RuleValidationException($"Rule '{ruleId}': condition needs a fact.");
        if (c.Op is null || !Ops.Contains(c.Op.ToLowerInvariant()))
            throw new RuleValidationException($"Rule '{ruleId}': unknown op '{c.Op}'. Allowed: {string.Join(", ", Ops)}.");
        if (c.Op.ToLowerInvariant() != "exists" && c.Value is null)
            throw new RuleValidationException($"Rule '{ruleId}': op '{c.Op}' needs a value.");
    }

    public RulesEvaluation Evaluate(RuleSet set, IReadOnlyDictionary<string, object?> facts)
    {
        var hits = set.Rules
            .Where(r => r.Enabled && Matches(r.When, facts))
            .OrderBy(r => r.Priority)
            .Select(r => new RuleHit(r.Id, r.Outcome, r.Priority, r.Description))
            .ToList();

        if (hits.Count == 0)
            return new RulesEvaluation(set.DefaultOutcome, hits, "DEFAULT", set.Version, facts);

        var outcome = hits.Max(h => h.Outcome);
        var deciding = hits.First(h => h.Outcome == outcome);
        return new RulesEvaluation(outcome, hits, deciding.Id, set.Version, facts);
    }

    /// <summary>Flattens a unified score + application into the fact bag rules can reference.</summary>
    public static Dictionary<string, object?> BuildFacts(UnifiedRiskScore score, UnifiedRiskInput input, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var facts = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["score"] = score.Score,
            ["tier"] = score.Tier,
            ["recommendedAction"] = score.RecommendedAction,
            ["coveragePercent"] = score.CoveragePercent,
            ["hardStops"] = score.HardStops,
            ["reasonCodes"] = score.ReasonCodes.Select(r => r.Code).ToList(),
            ["highSeverityReasons"] = score.ReasonCodes.Count(r => r.Severity == RiskTier.High),
            ["mediumSeverityReasons"] = score.ReasonCodes.Count(r => r.Severity == RiskTier.Medium),
            ["coverageGaps"] = score.CoverageGaps,
            ["matchFound"] = input.MatchFound ?? input.Application?.MatchFound,
            ["mcc"] = input.Application is null ? null : (int)input.Application.MerchantCategoryCode,
            ["annualVolume"] = input.Application?.AnnualVolume,
            ["averageTicket"] = input.Application?.AverageTicket,
            ["highestTicket"] = input.Application?.HighestTicket,
            ["existingRelationship"] = input.Application?.ExistingRelationship,
            ["kybRisk"] = input.KybRisk?.ToString(),
            ["businessVerified"] = input.BusinessVerified,
            ["entityAgeMonths"] = input.EntityAgeMonths,
            ["sanctionsMatch"] = input.SanctionsMatch,
            ["pepMatch"] = input.PepMatch,
            ["adverseMedia"] = input.AdverseMedia,
            ["prohibitedVerdict"] = input.ProhibitedVerdict?.ToString(),
            ["websiteComplianceScore"] = input.WebsiteComplianceScore,
            ["volumePlausibilityScore"] = input.VolumePlausibilityScore,
            ["termsRiskBand"] = input.TermsRiskBand,
            ["creditDecision"] = input.CreditDecision?.Decision.ToString(),
            ["approveProbability"] = input.CreditDecision?.Probabilities.GetValueOrDefault(CreditDecision.Decision.Approved)
        };
        if (extra is not null)
            foreach (var kv in extra) facts[kv.Key] = kv.Value;
        return facts;
    }

    internal static bool Matches(RuleCondition c, IReadOnlyDictionary<string, object?> facts)
    {
        if (c.All is not null) return c.All.All(x => Matches(x, facts));
        if (c.Any is not null) return c.Any.Any(x => Matches(x, facts));
        if (c.Not is not null) return !Matches(c.Not, facts);

        facts.TryGetValue(c.Fact!, out var actual);
        var op = c.Op!.ToLowerInvariant();
        if (op == "exists") return actual is not null;
        if (actual is null) return false;
        var expected = c.Value!.Value;

        switch (op)
        {
            case "contains":
            case "notcontains":
            {
                var found = actual switch
                {
                    string s => s.Contains(expected.ToString(), StringComparison.OrdinalIgnoreCase),
                    IEnumerable<string> list => list.Any(x => string.Equals(x, expected.ToString(), StringComparison.OrdinalIgnoreCase)),
                    _ => false
                };
                return op == "contains" ? found : !found;
            }
            case "in":
                return expected.ValueKind == JsonValueKind.Array && expected.EnumerateArray().Any(e => Compare(actual, e) == 0);
        }

        var cmp = Compare(actual, expected);
        if (cmp is null) return op == "neq";
        return op switch
        {
            "eq" => cmp == 0,
            "neq" => cmp != 0,
            "gt" => cmp > 0,
            "gte" => cmp >= 0,
            "lt" => cmp < 0,
            "lte" => cmp <= 0,
            _ => false
        };
    }

    private static int? Compare(object actual, JsonElement expected)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Number when TryToDouble(actual, out var a):
                return a.CompareTo(expected.GetDouble());
            case JsonValueKind.True or JsonValueKind.False when actual is bool b:
                return b.CompareTo(expected.GetBoolean());
            case JsonValueKind.String:
                var s = expected.GetString() ?? string.Empty;
                if (actual is bool ab && bool.TryParse(s, out var eb)) return ab.CompareTo(eb);
                if (TryToDouble(actual, out var ad) && double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ed)) return ad.CompareTo(ed);
                return string.Compare(actual.ToString(), s, StringComparison.OrdinalIgnoreCase);
            default:
                return null;
        }
    }

    private static bool TryToDouble(object value, out double result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l: result = l; return true;
            case float f: result = f; return true;
            case double d: result = d; return true;
            case decimal m: result = (double)m; return true;
            default: result = 0; return false;
        }
    }
}
