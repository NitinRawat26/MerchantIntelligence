using MerchantIntelligence.Kyb;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Profiling;

namespace MerchantIntelligence.Platform.Licensing;

/// <summary>Kind of permit or licence a merchant category may require to trade lawfully.</summary>
public enum LicenseType
{
    FoodService,
    Alcohol,
    Tobacco,
    Pharmacy,
    HealthcareProfessional,
    Legal,
    PersonalCare,
    ChildCare,
    MoneyServices,
    Gaming,
    Firearms,
    PassengerTransport,
    Contractor,
    Lodging,
    Other
}

/// <summary>
/// A licence or permit the analyst has seen, transcribed from the document the merchant supplied. Attested,
/// not verified: the platform records what was shown and checks it for completeness and expiry only.
/// </summary>
public sealed record LicenseAttestation(
    LicenseType Type,
    string? Number = null,
    string? IssuingAuthority = null,
    DateOnly? IssueDate = null,
    DateOnly? ExpiryDate = null,
    string? EvidenceReference = null);

/// <summary>One licence the merchant category calls for and what was found against it.</summary>
public sealed record LicenseRequirement(
    LicenseType Type,
    string Reason,
    RiskTier SeverityIfMissing,
    LicenseAttestation? Attested,
    string Status);

public sealed record LicensingAssessment(
    IReadOnlyList<LicenseRequirement> Requirements,
    IReadOnlyList<LicenseAttestation> Unrequested,
    IReadOnlyList<KybFlag> Flags,
    bool Covered);

/// <summary>
/// Regulated-MCC licence check on analyst-attested evidence. There is no national licence registry and the
/// county / state portals that exist are heterogeneous, so this step is conservative by design: it knows which
/// merchant categories cannot lawfully trade without a permit, asks for the permit, and checks that what was
/// shown is complete and in date. An active Secretary of State registration is <b>not</b> a licence and never
/// satisfies a requirement here.
/// </summary>
public static class LicensingAssessor
{
    public const int ExpiringSoonDays = 60;

    private static readonly IReadOnlyDictionary<int, (LicenseType Type, string Reason, RiskTier Severity)[]> ByMcc = Build();

    private static Dictionary<int, (LicenseType, string, RiskTier)[]> Build()
    {
        var d = new Dictionary<int, (LicenseType, string, RiskTier)[]>();
        void Add((LicenseType Type, string Reason, RiskTier Severity)[] req, params int[] mccs) { foreach (var m in mccs) d[m] = req; }

        var food = (LicenseType.FoodService, "Preparing or serving food requires a health-department food service permit.", RiskTier.Medium);
        var alcohol = (LicenseType.Alcohol, "Selling alcoholic beverages requires a state / local alcoholic-beverage licence.", RiskTier.High);
        Add([food], 5812, 5814, 5811, 5462, 5499);
        Add([food, alcohol], 5813);
        Add([alcohol], 5921);
        Add([(LicenseType.Tobacco, "Retailing tobacco or vapour products requires a tobacco retail licence.", RiskTier.Medium)], 5993);
        Add([(LicenseType.Pharmacy, "Dispensing prescription drugs requires a state board of pharmacy licence.", RiskTier.High)], 5912, 5122);
        Add([(LicenseType.HealthcareProfessional, "Clinical services require the practitioner's state professional licence.", RiskTier.High)],
            8011, 8021, 8031, 8041, 8042, 8043, 8049, 8050, 8062, 8071, 8099);
        Add([(LicenseType.Legal, "Legal services require the attorney's bar admission.", RiskTier.Medium)], 8111);
        Add([(LicenseType.PersonalCare, "Barber, cosmetology and massage services require a state board licence for the establishment and its practitioners.", RiskTier.Medium)], 7230, 7297, 7298);
        Add([(LicenseType.ChildCare, "Child-care providers require a state child-care licence.", RiskTier.High)], 8351);
        Add([(LicenseType.MoneyServices, "Money transmission, currency exchange or crypto-asset dealing requires FinCEN MSB registration and state money-transmitter licences.", RiskTier.High)], 6051, 6211, 4829, 6540);
        Add([(LicenseType.Gaming, "Gambling and betting require a gaming-commission licence in the operating jurisdiction.", RiskTier.High)], 7995, 7800, 7801, 7802);
        Add([(LicenseType.Firearms, "Firearms dealing requires a Federal Firearms License.", RiskTier.High)], 5099);
        Add([(LicenseType.PassengerTransport, "Taxi, limousine and passenger-transport operators require a local operating permit.", RiskTier.Low)], 4121, 4111, 4131);
        Add([(LicenseType.Contractor, "General and trade contractors require a state contractor licence in most states.", RiskTier.Low)], 1520, 1711, 1731, 1740, 1750, 1761, 1771, 1799);
        Add([(LicenseType.Lodging, "Lodging requires a local operating / occupancy permit.", RiskTier.Low)], 7011, 7012);
        return d;
    }

    public static bool IsRegulated(int mcc) => ByMcc.ContainsKey(mcc);

    public static LicensingAssessment Assess(AssessmentIntake intake, MerchantProfile? profile, DateOnly today)
    {
        var attested = intake.Licenses ?? [];
        var flags = new List<KybFlag>();
        var requirements = new List<LicenseRequirement>();
        var required = ByMcc.TryGetValue(intake.MerchantCategoryCode, out var r) ? r : [];
        var isSmb = profile?.IsSmb == true;

        foreach (var (type, reason, severity) in required)
        {
            var match = attested.FirstOrDefault(a => a.Type == type);
            string status;
            if (match is null)
            {
                status = "Missing";
                // A small merchant rarely has a compliance function; a missing permit is the analyst's finding to chase, not a decline.
                flags.Add(new($"LICENSE_MISSING_{type.ToString().ToUpperInvariant()}",
                    $"MCC {intake.MerchantCategoryCode} requires a {Describe(type)} and none was attested. {reason}", severity));
            }
            else
            {
                status = Judge(match, today, flags);
            }
            requirements.Add(new(type, reason, severity, match, status));
        }

        var unrequested = attested.Where(a => required.All(x => x.Type != a.Type)).ToList();
        foreach (var extra in unrequested)
            Judge(extra, today, flags);

        if (required.Length > 0 && isSmb && attested.Count == 0)
            flags.Add(new("LICENSE_SMB_NO_EVIDENCE",
                "No licence evidence at all for a regulated small merchant; ask for the permit before boarding rather than after.", RiskTier.Low));

        var covered = required.Length == 0 || requirements.All(q => q.Attested is not null);
        return new LicensingAssessment(requirements, unrequested, Dedupe(flags), covered);
    }

    private static string Judge(LicenseAttestation a, DateOnly today, List<KybFlag> flags)
    {
        var code = a.Type.ToString().ToUpperInvariant();
        if (a.ExpiryDate is { } exp && exp < today)
        {
            flags.Add(new($"LICENSE_EXPIRED_{code}", $"{Describe(a.Type)}{Num(a)} expired on {exp:yyyy-MM-dd}.", RiskTier.High));
            return "Expired";
        }
        if (a.IssueDate is { } iss && iss > today)
        {
            flags.Add(new($"LICENSE_FUTURE_ISSUE_{code}", $"{Describe(a.Type)}{Num(a)} carries an issue date in the future ({iss:yyyy-MM-dd}); check the transcription.", RiskTier.Medium));
            return "Inconsistent";
        }
        var incomplete = string.IsNullOrWhiteSpace(a.Number) || string.IsNullOrWhiteSpace(a.IssuingAuthority);
        if (incomplete)
            flags.Add(new($"LICENSE_INCOMPLETE_{code}", $"{Describe(a.Type)} attested without {(string.IsNullOrWhiteSpace(a.Number) ? "a licence number" : "an issuing authority")}; it cannot be re-checked later.", RiskTier.Low));
        if (string.IsNullOrWhiteSpace(a.EvidenceReference))
            flags.Add(new($"LICENSE_UNEVIDENCED_{code}", $"{Describe(a.Type)}{Num(a)} was attested without a document reference; attach the permit or note where it was sighted.", RiskTier.Low));
        if (a.ExpiryDate is { } soon && soon.DayNumber - today.DayNumber <= ExpiringSoonDays)
        {
            flags.Add(new($"LICENSE_EXPIRING_{code}", $"{Describe(a.Type)}{Num(a)} expires on {soon:yyyy-MM-dd}, within {ExpiringSoonDays} days; diarise renewal evidence.", RiskTier.Low));
            return "Expiring soon";
        }
        return incomplete ? "Attested (incomplete)" : "Attested";
    }

    private static string Num(LicenseAttestation a) => string.IsNullOrWhiteSpace(a.Number) ? "" : $" {a.Number}";

    private static List<KybFlag> Dedupe(List<KybFlag> flags) => flags.GroupBy(f => f.Code).Select(g => g.First()).ToList();

    public static string Describe(LicenseType t) => t switch
    {
        LicenseType.FoodService => "food service permit",
        LicenseType.Alcohol => "alcoholic-beverage licence",
        LicenseType.Tobacco => "tobacco retail licence",
        LicenseType.Pharmacy => "pharmacy licence",
        LicenseType.HealthcareProfessional => "professional healthcare licence",
        LicenseType.Legal => "bar admission",
        LicenseType.PersonalCare => "personal-care establishment licence",
        LicenseType.ChildCare => "child-care licence",
        LicenseType.MoneyServices => "money-services registration / licence",
        LicenseType.Gaming => "gaming licence",
        LicenseType.Firearms => "federal firearms licence",
        LicenseType.PassengerTransport => "passenger-transport permit",
        LicenseType.Contractor => "contractor licence",
        LicenseType.Lodging => "lodging operating permit",
        _ => "licence"
    };
}
