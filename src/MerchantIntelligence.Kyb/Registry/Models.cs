using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.Kyb.Registry;

public sealed record BusinessIdentity(
    string LegalName,
    string? TradingName = null,
    string? RegistrationNumber = null,
    string? TaxId = null,
    string? AddressLine = null,
    string? City = null,
    string? Region = null,
    string? PostalCode = null,
    string? Country = null,
    string? WebsiteUrl = null)
{
    public string FullAddress => string.Join(", ",
        new[] { AddressLine, City, Region, PostalCode, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed record RegistryRecord(
    string Source,
    string SourceId,
    string LegalName,
    string? Status,
    DateOnly? IncorporationDate,
    string? Jurisdiction,
    string? RegistrationNumber,
    string? Address,
    string? EntityType,
    Uri? SourceUrl,
    IReadOnlyDictionary<string, string>? Extra = null);

public sealed record RegistryMatch(
    RegistryRecord Record,
    double NameScore,
    double AddressScore,
    double OverallScore);

public sealed record RegistrySourceResult(
    string Source,
    bool Succeeded,
    IReadOnlyList<RegistryMatch> Matches,
    string? Error = null);

public sealed record AddressVerification(
    string Provider,
    bool Verified,
    string? MatchedAddress,
    double? Latitude,
    double? Longitude,
    string? Error = null);

public enum VerificationStatus
{
    Verified,
    PartialMatch,
    NotFound,
    Inconclusive
}

public sealed record KybFlag(string Code, string Message, RiskTier Severity);

public sealed record BusinessVerificationResult(
    BusinessIdentity Input,
    VerificationStatus Status,
    double ConfidencePercent,
    RegistryMatch? BestMatch,
    int? EntityAgeMonths,
    AddressVerification? Address,
    IReadOnlyList<RegistrySourceResult> Sources,
    IReadOnlyList<KybFlag> Flags);
