using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.Kyb.Sanctions;

public enum SanctionedEntityType
{
    Person,
    Organization,
    Vessel,
    Aircraft,
    Unknown
}

public sealed record SanctionedEntity(
    string Id,
    string ListName,
    SanctionedEntityType Type,
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> BirthDates,
    IReadOnlyList<string> Countries,
    IReadOnlyList<string> Programs,
    string? Remarks,
    Uri? SourceUrl)
{
    public IEnumerable<string> AllNames => Aliases.Prepend(Name);
}

public sealed record ScreeningSubject(
    string Name,
    DateOnly? DateOfBirth = null,
    string? Country = null,
    bool IsIndividual = false,
    string? Role = null);

public sealed record SanctionsHit(
    SanctionedEntity Entity,
    string MatchedName,
    double NameScore,
    double Score,
    IReadOnlyList<string> Reasons);

/// <summary>
/// One article/record about a subject. <paramref name="Tone"/> is <c>negative</c> when the subject and a risk term share a
/// sentence or the headline, <c>mention</c> when both appear in the article but not together, otherwise <c>neutral</c>.
/// </summary>
public sealed record AdverseMediaArticle(
    string Title,
    Uri Url,
    string Source,
    DateTimeOffset? Published,
    string Tone,
    string? Snippet = null,
    IReadOnlyList<string>? MatchedTerms = null,
    string? Category = null,
    string? Context = null,
    string? Provider = null);

public sealed record AdverseMediaProviderStatus(string Provider, bool Succeeded, int ArticleCount, string? Error);

/// <summary><paramref name="Succeeded"/> is true when at least one source answered; <paramref name="Providers"/> shows which did.</summary>
public sealed record AdverseMediaResult(
    string Provider,
    bool Succeeded,
    int ArticleCount,
    int NegativeCount,
    IReadOnlyList<AdverseMediaArticle> Articles,
    string? Error = null,
    IReadOnlyList<AdverseMediaProviderStatus>? Providers = null,
    int MentionCount = 0);

public sealed record SubjectScreeningResult(
    ScreeningSubject Subject,
    bool PotentialMatch,
    IReadOnlyList<SanctionsHit> Hits,
    AdverseMediaResult? AdverseMedia,
    IReadOnlyList<KybScreeningFlag> Flags);

public sealed record KybScreeningFlag(string Code, string Message, RiskTier Severity);

public sealed record ScreeningReport(
    IReadOnlyList<SubjectScreeningResult> Subjects,
    IReadOnlyList<SanctionsListStatus> Lists,
    RiskTier OverallRisk,
    IReadOnlyList<KybScreeningFlag> Flags);

public sealed record SanctionsListStatus(string ListName, int EntityCount, DateTimeOffset? LoadedAt, string? Error);
