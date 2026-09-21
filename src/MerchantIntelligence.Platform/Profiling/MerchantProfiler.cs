using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Assessment;

namespace MerchantIntelligence.Platform.Profiling;

/// <summary>Result of the entity step: the legal form the run will assume and why.</summary>
public sealed record EntityAssessment(
    EntityType EntityType,
    bool Inferred,
    RegistryScope RegistryScope,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<ProfileFinding> Findings);

/// <summary>
/// Deterministic, lookup-free classification of the applicant. Entity type sets a ceiling / floor on the size
/// segment; the segment and legal form together decide which registries are worth asking and which steps do
/// not apply. Mismatches between the declared form and the rest of the intake become findings, never silent
/// overrides.
/// </summary>
public sealed class MerchantProfiler
{
    public const decimal MicroVolumeCeiling = 250_000m;
    public const decimal SmallVolumeCeiling = 1_000_000m;
    public const decimal MidVolumeCeiling = 10_000_000m;
    public const int MicroEmployeeCeiling = 5;
    public const int SmallEmployeeCeiling = 20;
    public const int MidEmployeeCeiling = 100;
    public const int ChainLocationThreshold = 10;

    private static readonly HashSet<int> NonProfitMccs = [8398, 8661, 8641, 8651, 8699, 8220, 8211];

    public EntityAssessment ClassifyEntity(AssessmentIntake i)
    {
        var reasons = new List<string>();
        var findings = new List<ProfileFinding>();
        var owners = i.Owners.Count;
        var type = i.EntityType ?? EntityType.Unknown;
        var inferred = false;

        if (type == EntityType.Unknown)
        {
            inferred = true;
            var name = i.Business.LegalName;
            if (Has(name, " inc", " corp", " corporation", " incorporated", " plc", " ltd", " limited", " ag", " sa", " nv"))
            {
                type = i.AnnualVolume >= MidVolumeCeiling || (i.EmployeeCount ?? 0) > MidEmployeeCeiling ? EntityType.PublicCorporation : EntityType.CCorporation;
                reasons.Add($"Legal name suffix suggests a corporation; {(type == EntityType.PublicCorporation ? "size points to a public filer" : "treated as a private corporation")}.");
            }
            else if (Has(name, " llc", " l.l.c", " pllc"))
            {
                type = owners > 1 ? EntityType.MultiMemberLlc : EntityType.SingleMemberLlc;
                reasons.Add($"Legal name suffix 'LLC' with {owners} declared owner(s).");
            }
            else if (Has(name, " llp", " partners", " partnership", " & sons", " and sons"))
            {
                type = EntityType.Partnership;
                reasons.Add("Legal name suggests a partnership.");
            }
            else if (NonProfitMccs.Contains(i.MerchantCategoryCode) || Has(name, " foundation", " charity", " church", " association", " society"))
            {
                type = EntityType.NonProfit;
                reasons.Add("MCC or legal name points to a non-profit.");
            }
            else if (owners == 1 && (i.Owners[0].OwnershipPercent ?? 100) >= 99 && (i.EmployeeCount ?? 0) <= MicroEmployeeCeiling)
            {
                type = EntityType.SoleProprietorship;
                reasons.Add("Single 100% owner, no corporate suffix and micro headcount.");
            }
            else
            {
                type = EntityType.Other;
                reasons.Add("No entity type declared and none could be inferred from the legal name.");
            }
            findings.Add(new("ENTITY_TYPE_INFERRED", $"Entity type not declared; assuming {Describe(type)} from the application.", RiskTier.Low));
        }
        else
            reasons.Add($"Declared as {Describe(type)}.");

        // Consistency between the legal form and the rest of the application.
        if (type == EntityType.SoleProprietorship)
        {
            if (owners > 1)
                findings.Add(new("ENTITY_OWNER_MISMATCH", $"A sole proprietorship has one owner but {owners} were declared.", RiskTier.Medium));
            if (i.EmployeeCount is > SmallEmployeeCeiling)
                findings.Add(new("ENTITY_SIZE_MISMATCH", $"Sole proprietorship declaring {i.EmployeeCount} employees is unusual; confirm the legal form.", RiskTier.Medium));
            if (i.AnnualVolume >= SmallVolumeCeiling)
                findings.Add(new("ENTITY_VOLUME_MISMATCH", $"Sole proprietorship declaring {i.AnnualVolume:C0} annual card volume; volume this size is normally incorporated.", RiskTier.Medium));
        }
        if (type == EntityType.SingleMemberLlc && owners > 1)
            findings.Add(new("ENTITY_OWNER_MISMATCH", $"Single-member LLC declared with {owners} owners.", RiskTier.Medium));
        if (type is EntityType.MultiMemberLlc or EntityType.Partnership && owners <= 1)
            findings.Add(new("ENTITY_OWNER_MISMATCH", $"{Describe(type)} declared with {owners} owner(s); list every owner at or above 25%.", RiskTier.Medium));
        if (type == EntityType.SCorporation)
        {
            if (owners > 100)
                findings.Add(new("ENTITY_OWNER_MISMATCH", "S-Corporations are limited to 100 shareholders.", RiskTier.Medium));
            var foreign = i.Owners.Where(o => o.Nationality is { } n && !n.Equals("US", StringComparison.OrdinalIgnoreCase) && !n.Equals("USA", StringComparison.OrdinalIgnoreCase)).ToList();
            if (foreign.Count > 0)
                findings.Add(new("ENTITY_OWNER_MISMATCH", $"S-Corporation with non-US shareholder(s): {string.Join(", ", foreign.Select(f => f.FullName))}.", RiskTier.Medium));
        }
        if (type == EntityType.PublicCorporation && i.AnnualVolume < SmallVolumeCeiling)
            findings.Add(new("ENTITY_VOLUME_MISMATCH", $"Public corporation declaring only {i.AnnualVolume:C0} card volume.", RiskTier.Low));
        if (type == EntityType.NonProfit && !NonProfitMccs.Contains(i.MerchantCategoryCode))
            findings.Add(new("ENTITY_MCC_MISMATCH", $"Non-profit declared with commercial MCC {i.MerchantCategoryCode}; confirm the activity is exempt-purpose.", RiskTier.Low));
        if (i.Owners.Sum(o => o.OwnershipPercent ?? 0) > 100.5)
            findings.Add(new("OWNERSHIP_OVER_100", $"Declared ownership sums to {i.Owners.Sum(o => o.OwnershipPercent ?? 0):F0}%.", RiskTier.Medium));

        var scope = type switch
        {
            EntityType.SoleProprietorship => RegistryScope.None,
            EntityType.Government => RegistryScope.None,
            EntityType.NonProfit => RegistryScope.TaxExempt,
            EntityType.PublicCorporation => RegistryScope.Global,
            EntityType.CCorporation when i.AnnualVolume >= MidVolumeCeiling => RegistryScope.Global,
            EntityType.Unknown or EntityType.Other => RegistryScope.Global,
            _ => RegistryScope.Local
        };
        reasons.Add(scope switch
        {
            RegistryScope.None => "No company register holds this form; identity rests on the owner, local presence and bank evidence.",
            RegistryScope.TaxExempt => "Verify against tax-exempt registers (IRS TEOS); company registers are secondary.",
            RegistryScope.Local => "Verify against state / national company registers; LEI and SEC filings are not expected.",
            _ => "Verify against global identifiers and filings (GLEIF, SEC EDGAR) as well as company registers."
        });

        return new EntityAssessment(type, inferred, scope, reasons, findings);
    }

    public MerchantProfile Profile(AssessmentIntake i, EntityAssessment entity, bool hasWebsite, bool hasBankInput, bool hasFinancialInput)
    {
        var reasons = new List<string>(entity.Reasons);
        var findings = new List<ProfileFinding>(entity.Findings);
        var locations = Math.Max(1, i.LocationCount ?? 1);

        var byVolume = i.AnnualVolume switch
        {
            < MicroVolumeCeiling => MerchantSegment.Micro,
            < SmallVolumeCeiling => MerchantSegment.Small,
            < MidVolumeCeiling => MerchantSegment.Mid,
            _ => MerchantSegment.Enterprise
        };
        var segment = byVolume;
        reasons.Add($"Annual card volume {i.AnnualVolume:C0} → {byVolume}.");

        if (i.EmployeeCount is { } emp)
        {
            var byEmployees = emp switch
            {
                <= MicroEmployeeCeiling => MerchantSegment.Micro,
                <= SmallEmployeeCeiling => MerchantSegment.Small,
                <= MidEmployeeCeiling => MerchantSegment.Mid,
                _ => MerchantSegment.Enterprise
            };
            if (byEmployees > segment)
            {
                reasons.Add($"{emp} employees → {byEmployees}; headcount lifts the segment.");
                segment = byEmployees;
            }
            else
                reasons.Add($"{emp} employees is consistent with {segment}.");
        }

        if (locations >= ChainLocationThreshold && segment < MerchantSegment.Mid)
        {
            reasons.Add($"{locations} locations behaves like a chain; lifted to Mid.");
            segment = MerchantSegment.Mid;
        }
        else if (locations > 1)
            reasons.Add($"{locations} locations: presence and volume are checked per location; segment unchanged.");

        // Entity type acts as a ceiling / floor.
        switch (entity.EntityType)
        {
            case EntityType.SoleProprietorship when segment > MerchantSegment.Small:
                reasons.Add("Sole proprietorship capped at Small; the declared size is recorded as a finding.");
                segment = MerchantSegment.Small;
                break;
            case EntityType.SCorporation when segment == MerchantSegment.Enterprise:
                reasons.Add("S-Corporation capped at Mid (100-shareholder limit).");
                segment = MerchantSegment.Mid;
                break;
            case EntityType.PublicCorporation when segment < MerchantSegment.Mid:
                reasons.Add("Public corporation floored at Mid.");
                segment = MerchantSegment.Mid;
                break;
        }

        var notApplicable = new List<StepApplicability>();
        var isSmb = segment is MerchantSegment.Micro or MerchantSegment.Small;
        if (isSmb && !hasFinancialInput)
            notApplicable.Add(new("financials", $"{segment} merchants are not expected to produce audited P&L / balance sheets; bank-statement cash flow is the financial evidence instead."));
        if (isSmb && !hasWebsite && (i.HasPhysicalLocation ?? true))
        {
            notApplicable.Add(new("website", $"{segment} card-present merchant without a website; card-brand website rules do not apply."));
            notApplicable.Add(new("mcc", "MCC validation needs website evidence, which a card-present merchant without a site does not have."));
        }
        if (entity.EntityType == EntityType.Government)
        {
            notApplicable.Add(new("verification", "Public bodies are not held in company registers; identity is established from the authority's charter."));
            notApplicable.Add(new("credit", "The merchant credit model is trained on commercial applicants and does not apply to public bodies."));
        }
        if (entity.RegistryScope == RegistryScope.None && entity.EntityType == EntityType.SoleProprietorship)
            reasons.Add("Registry verification runs against company registers only to rule out an undisclosed entity; 'not found' is expected and is not penalised.");

        if (isSmb && !hasBankInput)
            findings.Add(new("SMB_NO_BANK_STATEMENT", $"A bank statement is the primary financial evidence for a {segment} merchant and none was supplied.", RiskTier.Medium));
        if (isSmb && i.Owners.Count == 0)
            findings.Add(new("SMB_NO_OWNER", $"A {segment} merchant is its owner; at least one principal must be declared for identity and screening.", RiskTier.Medium));
        if (locations > 1 && i.AnnualVolume / locations < 20_000m)
            findings.Add(new("LOW_VOLUME_PER_LOCATION", $"{i.AnnualVolume / locations:C0} per location across {locations} locations is very low; confirm all locations trade.", RiskTier.Low));

        return new MerchantProfile(entity.EntityType, entity.Inferred, segment, entity.RegistryScope, locations, reasons, notApplicable, findings);
    }

    public static string Describe(EntityType t) => t switch
    {
        EntityType.SoleProprietorship => "sole proprietorship",
        EntityType.SingleMemberLlc => "single-member LLC",
        EntityType.MultiMemberLlc => "multi-member LLC",
        EntityType.Partnership => "partnership / LLP",
        EntityType.SCorporation => "S-Corporation",
        EntityType.CCorporation => "private C-Corporation",
        EntityType.PublicCorporation => "public corporation",
        EntityType.NonProfit => "non-profit",
        EntityType.Government => "government / public body",
        EntityType.Trust => "trust",
        EntityType.Other => "other legal form",
        _ => "unknown legal form"
    };

    private static bool Has(string name, params string[] suffixes)
    {
        var n = " " + name.ToLowerInvariant().Replace(",", " ").Replace(".", "") + " ";
        return suffixes.Any(s => n.Contains(s.Replace(".", "") + " ", StringComparison.Ordinal));
    }
}
