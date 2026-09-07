using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.MccValidation.Validation;

public enum MccVerdict
{
    /// <summary>Evidence supports the declared MCC.</summary>
    Consistent,
    /// <summary>Evidence points elsewhere but the declared MCC is a plausible neighbour.</summary>
    Questionable,
    /// <summary>Evidence contradicts the declared MCC.</summary>
    Inconsistent,
    /// <summary>Not enough usable evidence to judge.</summary>
    Insufficient
}

public sealed record MccCandidate(int Mcc, string Description, double Score);

public sealed record RiskFlag(string Code, string Message, RiskTier Severity);

/// <summary>Output of one independent evidence provider.</summary>
public sealed record ProviderEvidence(
    string Provider,
    bool Succeeded,
    IReadOnlyList<MccCandidate> Candidates,
    IReadOnlyList<string> Highlights,
    string? Error = null)
{
    public static ProviderEvidence Failed(string provider, string error) =>
        new(provider, false, Array.Empty<MccCandidate>(), Array.Empty<string>(), error);

    public double ScoreFor(int mcc) => Candidates.FirstOrDefault(c => c.Mcc == mcc)?.Score ?? 0;
}

public sealed record MccValidationResult(
    int DeclaredMcc,
    string DeclaredDescription,
    RiskTier DeclaredRiskTier,
    Uri WebsiteUrl,
    MccVerdict Verdict,
    double AccuracyPercent,
    IReadOnlyList<MccCandidate> SuggestedMccs,
    IReadOnlyList<RiskFlag> RiskFlags,
    IReadOnlyList<ProviderEvidence> Evidence,
    IReadOnlyList<Uri> PagesAnalyzed);
