using MerchantIntelligence.Kyb;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Profiling;

namespace MerchantIntelligence.Platform.Owners;

/// <summary>What is known about one declared principal after the identity checks.</summary>
public sealed record OwnerCheck(
    string FullName,
    string? Role,
    int? Age,
    bool DateOfBirthDeclared,
    bool NationalityDeclared,
    bool AddressDeclared,
    /// <summary>Owner's address is the business address (home-based trading).</summary>
    bool? SharesBusinessAddress,
    /// <summary>Earlier applications by the same person (name + date of birth) for a different business.</summary>
    IReadOnlyList<PriorApplication> PriorApplications);

public sealed record PriorApplication(string AssessmentId, string MerchantName, DateTimeOffset SeenAt);

public sealed record OwnerAssessment(
    IReadOnlyList<OwnerCheck> Owners,
    /// <summary>Share of the identity attributes (DOB, nationality, address) supplied across all owners, 0..1.</summary>
    double CompletenessPercent,
    bool HomeBased,
    IReadOnlyList<KybFlag> Flags,
    /// <summary>False when no principal was declared, so nothing could be checked.</summary>
    bool Covered);

/// <summary>
/// Deterministic identity-depth checks on declared principals. For a Micro / Small merchant the business is its owner, so
/// gaps that are cosmetic for a listed company (missing date of birth, no address) are material here and are weighted up.
/// Nothing in this class talks to an external source; the registered agent returned by a state register is never treated as
/// an owner.
/// </summary>
public static class OwnerIdentityAssessor
{
    public const int MinimumAge = 18;
    /// <summary>A person could plausibly have founded the business no earlier than this age.</summary>
    public const int MinimumFoundingAge = 16;
    public const int VelocityWindowDays = 90;
    public const int VelocityThreshold = 3;

    public static OwnerAssessment Assess(AssessmentIntake intake, MerchantProfile? profile, Func<BeneficialOwner, IReadOnlyList<PriorApplication>> history, DateOnly today)
    {
        var isSmb = profile?.IsSmb == true;
        var flags = new List<KybFlag>();
        var checks = new List<OwnerCheck>();
        if (intake.Owners.Count == 0)
            return new OwnerAssessment(checks, 0, false, flags, false);

        var declared = 0; var possible = 0;
        var incomplete = new List<string>();
        var homeBased = false;

        foreach (var o in intake.Owners)
        {
            var age = o.DateOfBirth is { } dob ? AgeOn(dob, today) : (int?)null;
            var prior = history(o);
            var shares = o.Address is { } addr && intake.Business.AddressLine is { } biz
                ? AddressMatcher.Similarity(addr, string.Join(", ", new[] { biz, intake.Business.City, intake.Business.PostalCode }.Where(s => !string.IsNullOrWhiteSpace(s)))) >= 0.85
                    || AddressMatcher.Similarity(addr, biz) >= 0.85
                : (bool?)null;
            homeBased |= shares == true;

            possible += 3;
            declared += (o.DateOfBirth is null ? 0 : 1) + (string.IsNullOrWhiteSpace(o.Nationality) ? 0 : 1) + (string.IsNullOrWhiteSpace(o.Address) ? 0 : 1);
            var missing = new List<string>();
            if (o.DateOfBirth is null) missing.Add("date of birth");
            if (string.IsNullOrWhiteSpace(o.Nationality)) missing.Add("nationality");
            if (string.IsNullOrWhiteSpace(o.Address)) missing.Add("address");
            if (missing.Count > 0) incomplete.Add($"{o.FullName} ({string.Join(", ", missing)})");

            if (age is { } a && a < MinimumAge)
                flags.Add(new("OWNER_UNDERAGE", $"{o.FullName} is {a} years old; a principal must be at least {MinimumAge}.", RiskTier.High));
            if (age is { } a2 && intake.YearsInBusiness is { } years && years > a2 - MinimumFoundingAge)
                flags.Add(new("OWNER_AGE_VS_TENURE", $"{o.FullName} is {a2}; {years:0.#} years in business would mean founding the business before age {MinimumFoundingAge}.", RiskTier.Medium));

            var recent = prior.Count(p => p.SeenAt >= today.ToDateTime(TimeOnly.MinValue).AddDays(-VelocityWindowDays));
            if (recent >= VelocityThreshold)
                flags.Add(new("OWNER_APPLICATION_VELOCITY", $"{o.FullName} appears on {recent} other application(s) in the last {VelocityWindowDays} days ({string.Join("; ", prior.Take(3).Select(p => p.MerchantName))}).", RiskTier.High));
            else if (prior.Count > 0)
                flags.Add(new("OWNER_DUPLICATE_APPLICATION", $"{o.FullName} was declared as a principal on {prior.Count} earlier application(s): {string.Join("; ", prior.Take(3).Select(p => $"{p.MerchantName} ({p.SeenAt:yyyy-MM-dd})"))}.", RiskTier.Medium));

            checks.Add(new OwnerCheck(o.FullName, o.Role, age, o.DateOfBirth is not null, !string.IsNullOrWhiteSpace(o.Nationality), !string.IsNullOrWhiteSpace(o.Address), shares, prior));
        }

        if (incomplete.Count > 0)
            flags.Add(new("OWNER_IDENTITY_INCOMPLETE",
                $"Identity attributes missing for {string.Join("; ", incomplete)}.{(isSmb ? " For a small merchant the owner is the primary identity evidence." : "")}",
                isSmb ? RiskTier.Medium : RiskTier.Low));
        if (intake.Owners.All(o => string.IsNullOrWhiteSpace(o.Role)))
            flags.Add(new("OWNER_ROLE_UNDECLARED", "No principal has a declared role (owner, director, manager…); control cannot be established.", RiskTier.Low));

        var sum = intake.Owners.Sum(o => o.OwnershipPercent ?? 0);
        if (isSmb && intake.Owners.All(o => o.OwnershipPercent is not null) && sum < 75)
            flags.Add(new("OWNERSHIP_UNDER_DECLARED", $"Declared owners hold {sum:0.#}% in total; for a {profile!.Segment} merchant the remaining {100 - sum:0.#}% should be attributed to a named person.", RiskTier.Low));

        if (homeBased)
            flags.Add(new("OWNER_HOME_BASED", "A principal's residential address is the business address: the merchant trades from home.", RiskTier.Low));

        var completeness = possible == 0 ? 0 : (double)declared / possible;
        return new OwnerAssessment(checks, Math.Round(completeness, 3), homeBased, flags, true);
    }

    internal static int AgeOn(DateOnly dob, DateOnly today)
    {
        var age = today.Year - dob.Year;
        if (today < dob.AddYears(age)) age--;
        return age;
    }

    /// <summary>Key under which a person is remembered across applications: normalised name plus date of birth when known.</summary>
    public static string PrincipalKey(BeneficialOwner o) =>
        new string(o.FullName.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()) + "|" + (o.DateOfBirth?.ToString("yyyy-MM-dd") ?? "");
}
