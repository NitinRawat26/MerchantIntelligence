using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Profiling;

namespace MerchantIntelligence.Platform.Scoring;

/// <summary>A flag emitted by any upstream tool, normalised to code + severity.</summary>
public sealed record RiskSignal(string Source, string Code, string Message, RiskTier Severity);

/// <summary>
/// Everything the suite knows about an applicant. Every section is optional so a score can be produced
/// from whatever has been run so far; missing sections are reported as coverage gaps.
/// </summary>
public sealed record UnifiedRiskInput(
    MerchantApplication? Application = null,
    DecisionResult? CreditDecision = null,
    RiskTier? KybRisk = null,
    bool? BusinessVerified = null,
    int? EntityAgeMonths = null,
    bool? SanctionsMatch = null,
    bool? PepMatch = null,
    bool? AdverseMedia = null,
    BusinessPolicy? ProhibitedVerdict = null,
    int? WebsiteComplianceScore = null,
    int? VolumePlausibilityScore = null,
    string? TermsRiskBand = null,
    bool? MatchFound = null,
    IReadOnlyList<RiskSignal>? Signals = null,
    MerchantSegment? Segment = null,
    IReadOnlyCollection<string>? NotApplicableComponents = null);

/// <summary>
/// Component weights per merchant segment. Small merchants are judged more on who they are and whether the declared
/// volume fits a shop of that kind, less on a website they may not have; the credit model still carries most weight.
/// Weights only redistribute emphasis – the profile never lowers a component's score or lifts the approve threshold.
/// </summary>
public static class ScoreWeights
{
    public static readonly IReadOnlyDictionary<string, double> Standard = new Dictionary<string, double>
    {
        ["CreditModel"] = 0.30, ["Kyb"] = 0.20, ["Screening"] = 0.15, ["WebsiteCompliance"] = 0.10,
        ["VolumePlausibility"] = 0.10, ["BusinessPolicy"] = 0.10, ["Pricing"] = 0.05
    };

    public static readonly IReadOnlyDictionary<string, double> Smb = new Dictionary<string, double>
    {
        ["CreditModel"] = 0.25, ["Kyb"] = 0.25, ["Screening"] = 0.15, ["WebsiteCompliance"] = 0.05,
        ["VolumePlausibility"] = 0.15, ["BusinessPolicy"] = 0.10, ["Pricing"] = 0.05
    };

    public static IReadOnlyDictionary<string, double> For(MerchantSegment? segment) =>
        segment is MerchantSegment.Micro or MerchantSegment.Small ? Smb : Standard;

    /// <summary>Score components that a not-applicable step would have fed; they leave the denominator instead of counting as gaps.</summary>
    public static IEnumerable<string> ComponentsOfStep(string stepId) => stepId switch
    {
        "website" => ["WebsiteCompliance"],
        "credit" => ["CreditModel"],
        "plausibility" => ["VolumePlausibility"],
        "prohibited" => ["BusinessPolicy"],
        "terms" => ["Pricing"],
        _ => []
    };
}

public sealed record ScoreComponent(string Name, double Weight, double Score, double Weighted, string Detail, bool Covered);

public sealed record UnifiedReasonCode(string Code, string Description, RiskTier Severity, string Source);

public sealed record UnifiedRiskScore(
    int Score,                                   // 0 (worst) .. 1000 (best)
    string Tier,                                 // VeryLow | Low | Medium | High | VeryHigh
    string RecommendedAction,                    // Approve | Refer | Decline
    IReadOnlyList<ScoreComponent> Components,
    IReadOnlyList<UnifiedReasonCode> ReasonCodes,
    IReadOnlyList<string> CoverageGaps,
    IReadOnlyList<string> HardStops,
    double CoveragePercent);

/// <summary>
/// Combines the credit model, KYB, compliance, plausibility and pricing outputs into one 0–1000 score
/// with reason codes. Weighted average of per-component 0–100 scores, renormalised over the components
/// that were actually supplied, then hard stops (sanctions, prohibited business, MATCH) cap the score.
/// </summary>
public sealed class UnifiedRiskScorer
{
    private const double MissingComponentPenaltyPerGap = 0.03;
    private const double MinCoverageForApprove = 0.60;

    public UnifiedRiskScore Score(UnifiedRiskInput input)
    {
        var all = new List<ScoreComponent>();
        var reasons = new List<UnifiedReasonCode>();
        var hardStops = new List<string>();
        var w = ScoreWeights.For(input.Segment);

        AddCredit(input, all, reasons, w["CreditModel"]);
        AddKyb(input, all, reasons, w["Kyb"]);
        AddScreening(input, all, reasons, hardStops, w["Screening"]);
        AddProhibited(input, all, reasons, hardStops, w["BusinessPolicy"]);
        AddComponent(all, "WebsiteCompliance", w["WebsiteCompliance"], input.WebsiteComplianceScore,
            s => $"Website compliance {s}/100.");
        if (input.WebsiteComplianceScore is < 60)
            reasons.Add(new("WEBSITE_NON_COMPLIANT", $"Website compliance score {input.WebsiteComplianceScore}/100 below card-brand expectations.", RiskTier.Medium, "WebsiteCompliance"));
        AddComponent(all, "VolumePlausibility", w["VolumePlausibility"], input.VolumePlausibilityScore,
            s => $"Declared-volume plausibility {s}/100.");
        if (input.VolumePlausibilityScore is < 50)
            reasons.Add(new("VOLUME_IMPLAUSIBLE", $"Declared volume plausibility {input.VolumePlausibilityScore}/100.", RiskTier.Medium, "VolumePlausibility"));
        AddTerms(input, all, reasons, w["Pricing"]);

        // A component the profile ruled not applicable is neither evidence nor a gap: it leaves the denominator.
        var notApplicable = input.NotApplicableComponents ?? [];
        var components = all.Where(c => c.Covered || !notApplicable.Contains(c.Name)).ToList();

        if (input.MatchFound == true)
        {
            hardStops.Add("MATCH_LISTED");
            reasons.Add(new("MATCH_LISTED", "Listed on the terminated-merchant (MATCH) file.", RiskTier.High, "Match"));
        }

        foreach (var s in input.Signals ?? [])
            if (reasons.All(r => r.Code != s.Code))
                reasons.Add(new(s.Code, s.Message, s.Severity, s.Source));

        var covered = components.Where(c => c.Covered).ToList();
        var coverage = components.Count == 0 ? 0 : covered.Sum(c => c.Weight) / components.Sum(c => c.Weight);
        var raw = covered.Count == 0 ? 50 : covered.Sum(c => c.Weighted) / covered.Sum(c => c.Weight);

        // Uncovered components make us less certain, so drift the score towards the middle.
        var gaps = components.Where(c => !c.Covered).Select(c => c.Name).ToList();
        raw -= (raw - 50) * Math.Min(0.5, gaps.Count * MissingComponentPenaltyPerGap * 2);

        var score = (int)Math.Round(Math.Clamp(raw, 0, 100) * 10);
        if (hardStops.Count > 0) score = Math.Min(score, 150);
        else if (reasons.Any(r => r.Severity == RiskTier.High)) score = Math.Min(score, 549);

        var tier = score switch
        {
            >= 800 => "VeryLow",
            >= 650 => "Low",
            >= 450 => "Medium",
            >= 250 => "High",
            _ => "VeryHigh"
        };
        var action = hardStops.Count > 0 ? "Decline"
            : score >= 650 && coverage >= MinCoverageForApprove && reasons.All(r => r.Severity != RiskTier.High) ? "Approve"
            : score < 250 ? "Decline"
            : "Refer";

        var ordered = reasons.OrderByDescending(r => r.Severity).ThenBy(r => r.Code).ToList();
        return new UnifiedRiskScore(score, tier, action, components, ordered, gaps, hardStops, Math.Round(coverage * 100, 1));
    }

    private static void AddCredit(UnifiedRiskInput input, List<ScoreComponent> components, List<UnifiedReasonCode> reasons, double weight)
    {
        if (input.CreditDecision is null)
        {
            components.Add(new("CreditModel", weight, 0, 0, "Credit decision not run.", false));
            return;
        }
        var pApprove = input.CreditDecision.Probabilities.GetValueOrDefault(Decision.Approved);
        var s = pApprove * 100;
        components.Add(new("CreditModel", weight, Round(s), Round(s * weight), $"P(approve)={pApprove:P1}, predicted {input.CreditDecision.Decision}.", true));
        if (input.CreditDecision.Decision == Decision.Declined)
            reasons.Add(new("MODEL_DECLINE", $"Credit model predicts decline ({input.CreditDecision.Confidence:P0} confidence).", pApprove < 0.2 ? RiskTier.High : RiskTier.Medium, "CreditModel"));
        else if (input.CreditDecision.Decision == Decision.Cancelled)
            reasons.Add(new("MODEL_CANCEL_RISK", "Credit model predicts the merchant would be cancelled after boarding.", RiskTier.Medium, "CreditModel"));
    }

    private static void AddKyb(UnifiedRiskInput input, List<ScoreComponent> components, List<UnifiedReasonCode> reasons, double weight)
    {
        if (input.KybRisk is null && input.BusinessVerified is null)
        {
            components.Add(new("Kyb", weight, 0, 0, "KYB not run.", false));
            return;
        }
        var s = input.KybRisk switch { RiskTier.High => 20.0, RiskTier.Medium => 55.0, _ => 90.0 };
        if (input.KybRisk == RiskTier.High)
            reasons.Add(new("KYB_HIGH_RISK", "KYB report rates the business high risk.", RiskTier.High, "Kyb"));
        if (input.BusinessVerified == false)
        {
            s -= 25;
            reasons.Add(new("BUSINESS_UNVERIFIED", "Legal entity could not be verified against public registries.", RiskTier.High, "Kyb"));
        }
        if (input.EntityAgeMonths is < 12)
        {
            s -= 10;
            reasons.Add(new("NEW_ENTITY", $"Entity registered {input.EntityAgeMonths} months ago.", RiskTier.Medium, "Kyb"));
        }
        s = Math.Clamp(s, 0, 100);
        components.Add(new("Kyb", weight, Round(s), Round(s * weight), $"KYB overall risk {input.KybRisk?.ToString() ?? "n/a"}, verified={input.BusinessVerified?.ToString() ?? "n/a"}.", true));
    }

    private static void AddScreening(UnifiedRiskInput input, List<ScoreComponent> components, List<UnifiedReasonCode> reasons, List<string> hardStops, double weight)
    {
        if (input.SanctionsMatch is null && input.PepMatch is null && input.AdverseMedia is null)
        {
            components.Add(new("Screening", weight, 0, 0, "Sanctions/PEP/adverse-media screening not run.", false));
            return;
        }
        var s = 100.0;
        if (input.SanctionsMatch == true)
        {
            s = 0;
            hardStops.Add("SANCTIONS_MATCH");
            reasons.Add(new("SANCTIONS_MATCH", "Business or beneficial owner matches a sanctions list.", RiskTier.High, "Screening"));
        }
        if (input.PepMatch == true)
        {
            s -= 40;
            reasons.Add(new("PEP_MATCH", "Beneficial owner is a politically exposed person; enhanced due diligence required.", RiskTier.Medium, "Screening"));
        }
        if (input.AdverseMedia == true)
        {
            s -= 25;
            reasons.Add(new("ADVERSE_MEDIA", "Adverse media coverage found for the business or its owners.", RiskTier.Medium, "Screening"));
        }
        s = Math.Clamp(s, 0, 100);
        components.Add(new("Screening", weight, Round(s), Round(s * weight), "Sanctions/PEP/adverse-media screening.", true));
    }

    private static void AddProhibited(UnifiedRiskInput input, List<ScoreComponent> components, List<UnifiedReasonCode> reasons, List<string> hardStops, double weight)
    {
        if (input.ProhibitedVerdict is null)
        {
            components.Add(new("BusinessPolicy", weight, 0, 0, "Prohibited-business check not run.", false));
            return;
        }
        var (s, reason) = input.ProhibitedVerdict switch
        {
            BusinessPolicy.Prohibited => (0.0, new UnifiedReasonCode("PROHIBITED_BUSINESS", "Business type is prohibited under acceptable-use policy.", RiskTier.High, "BusinessPolicy")),
            BusinessPolicy.Restricted => (35.0, new UnifiedReasonCode("RESTRICTED_BUSINESS", "Business type requires licensing evidence or card-brand registration.", RiskTier.High, "BusinessPolicy")),
            BusinessPolicy.HighRisk => (60.0, new UnifiedReasonCode("HIGH_RISK_BUSINESS", "Business type is high-risk; enhanced due diligence applies.", RiskTier.Medium, "BusinessPolicy")),
            _ => (100.0, null)
        };
        if (input.ProhibitedVerdict == BusinessPolicy.Prohibited) hardStops.Add("PROHIBITED_BUSINESS");
        if (reason is not null) reasons.Add(reason);
        components.Add(new("BusinessPolicy", weight, s, Round(s * weight), $"Policy verdict {input.ProhibitedVerdict}.", true));
    }

    private static void AddTerms(UnifiedRiskInput input, List<ScoreComponent> components, List<UnifiedReasonCode> reasons, double weight)
    {
        if (string.IsNullOrEmpty(input.TermsRiskBand))
        {
            components.Add(new("Pricing", weight, 0, 0, "Terms recommendation not run.", false));
            return;
        }
        var s = input.TermsRiskBand.ToUpperInvariant() switch { "A" => 95.0, "B" => 80.0, "C" => 60.0, "D" => 35.0, _ => 15.0 };
        components.Add(new("Pricing", weight, s, Round(s * weight), $"Terms risk band {input.TermsRiskBand}.", true));
        if (s <= 35) reasons.Add(new("HEAVY_RESERVE_REQUIRED", $"Pricing band {input.TermsRiskBand} implies a significant reserve and settlement delay.", RiskTier.Medium, "Pricing"));
    }

    private static void AddComponent(List<ScoreComponent> components, string name, double weight, int? value, Func<int, string> detail)
    {
        if (value is null)
        {
            components.Add(new(name, weight, 0, 0, $"{name} not run.", false));
            return;
        }
        var s = Math.Clamp(value.Value, 0, 100);
        components.Add(new(name, weight, s, Round(s * weight), detail(s), true));
    }

    private static double Round(double v) => Math.Round(v, 2);
}
