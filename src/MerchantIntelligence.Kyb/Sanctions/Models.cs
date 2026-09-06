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

public sealed record AdverseMediaArticle(string Title, Uri Url, string Source, DateTimeOffset? Published, string Tone);

public sealed record AdverseMediaResult(
    string Provider,
    bool Succeeded,
    int ArticleCount,
    int NegativeCount,
    IReadOnlyList<AdverseMediaArticle> Articles,
    string? Error = null);

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
