using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Profiling;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Kyb;

public sealed class VerificationStep(BusinessVerificationService verification) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("verification", "Business identity verification",
        "Matches the legal entity against the public registries the profile says should hold it (state / national company registers for private companies, GLEIF and SEC EDGAR for public or large ones, none for sole proprietorships) and verifies the address.",
        [], [], false, []);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var scope = ScopeOf(ctx.Profile?.RegistryScope);
        ctx.Verification = await ctx.RunAsync(Descriptor, async () =>
            {
                var v = await verification.VerifyAsync(ctx.Intake.Business, scope, ctx.CancellationToken);
                var extraFlags = RegistryConsistencyFlags(ctx.Intake, v);
                return extraFlags.Count == 0 ? v : v with { Flags = v.Flags.Concat(extraFlags).ToList() };
            },
            v => $"{v.Status} ({v.ConfidencePercent:F0}% confidence) · registers: {Describe(scope)}{(v.BestMatch is null ? "" : $" · best match {v.BestMatch.Record.LegalName} via {v.BestMatch.Record.Source}")}");
    }

    internal static RegistryQueryScope ScopeOf(RegistryScope? scope) => scope switch
    {
        RegistryScope.Local => RegistryQueryScope.Local,
        RegistryScope.TaxExempt => RegistryQueryScope.TaxExempt,
        RegistryScope.None => RegistryQueryScope.None,
        _ => RegistryQueryScope.All
    };

    /// <summary>
    /// State registers (Kentucky SOS) publish an industry label and a headcount band alongside the record. Comparing them with the
    /// declared MCC and employee count catches applications that borrow a real company's name for a different business.
    /// </summary>
    internal static List<KybFlag> RegistryConsistencyFlags(AssessmentIntake intake, BusinessVerificationResult v)
    {
        var flags = new List<KybFlag>();
        if (v.BestMatch?.Record.Extra is not { } extra) return flags;

        if (extra.TryGetValue("employeeBand", out var band) && intake.EmployeeCount is int declared && ParseBand(band) is var (lo, hi))
        {
            if (declared < lo || (hi is int h && declared > h))
                flags.Add(new KybFlag("REGISTRY_HEADCOUNT_MISMATCH",
                    $"Register reports headcount band '{band}' but {declared} employees were declared.", RiskTier.Medium));
        }

        if (extra.TryGetValue("industry", out var industry) && IndustryMccRanges.TryGetValue(industry, out var ranges)
            && !ranges.Any(r => intake.MerchantCategoryCode >= r.From && intake.MerchantCategoryCode <= r.To))
            flags.Add(new KybFlag("REGISTRY_INDUSTRY_MISMATCH",
                $"Register industry '{industry}' does not correspond to declared MCC {intake.MerchantCategoryCode}.", RiskTier.Medium));

        if (extra.TryGetValue("lastAnnualReport", out var lar) && DateOnly.TryParse(lar, System.Globalization.CultureInfo.InvariantCulture, out var reported)
            && reported < DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-18))
            flags.Add(new KybFlag("REGISTRY_ANNUAL_REPORT_STALE",
                $"Last annual report on file is dated {reported:yyyy-MM-dd}; the entity may be lapsing.", RiskTier.Low));

        if (extra.TryGetValue("assumedName", out var assumed) && !string.IsNullOrWhiteSpace(intake.Business.TradingName)
            && MerchantIntelligence.Kyb.Matching.NameMatcher.Similarity(intake.Business.TradingName, assumed) >= 0.85)
            flags.Add(new KybFlag("REGISTRY_ASSUMED_NAME_MATCH",
                $"Trading name matches the assumed name '{assumed}' filed with the register.", RiskTier.Low));

        return flags;
    }

    private static (int, int?)? ParseBand(string band)
    {
        var m = System.Text.RegularExpressions.Regex.Match(band, @"(\d[\d,]*)\s*(?:-|to)\s*(\d[\d,]*)|(\d[\d,]*)\s*\+|over\s*(\d[\d,]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        int Num(string s) => int.Parse(s.Replace(",", ""));
        if (m.Groups[1].Success) return (Num(m.Groups[1].Value), Num(m.Groups[2].Value));
        return (Num(m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value), null);
    }

    /// <summary>SIC major-group labels as published by state registers → MCC ranges that plausibly belong to them.</summary>
    private static readonly Dictionary<string, (int From, int To)[]> IndustryMccRanges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Eating and Drinking Places"] = [(5811, 5814), (5462, 5462), (5499, 5499)],
        ["Food Stores"] = [(5411, 5499)],
        ["General Merchandise Stores"] = [(5300, 5399), (5310, 5311)],
        ["Apparel and Accessory Stores"] = [(5611, 5699)],
        ["Home Furniture, Furnishings, and Equipment Stores"] = [(5712, 5735)],
        ["Building Materials, Hardware, Garden Supply"] = [(5200, 5261)],
        ["Automotive Dealers and Gasoline Service Stations"] = [(5511, 5599)],
        ["Miscellaneous Retail"] = [(5900, 5999)],
        ["Personal Services"] = [(7210, 7299)],
        ["Business Services"] = [(7311, 7399)],
        ["Automotive Repair, Services, and Parking"] = [(7511, 7549)],
        ["Amusement and Recreation Services"] = [(7800, 7999)],
        ["Health Services"] = [(8011, 8099)],
        ["Legal Services"] = [(8111, 8111)],
        ["Educational Services"] = [(8211, 8299)],
        ["Hotels and Other Lodging Places"] = [(7011, 7012), (3501, 3999)],
        ["Construction"] = [(1520, 1799)],
        ["Real Estate"] = [(6513, 6513)],
    };

    private static string Describe(RegistryQueryScope scope) => scope switch
    {
        RegistryQueryScope.Local => "company registers authoritative, LEI / SEC absence expected",
        RegistryQueryScope.TaxExempt => "tax-exempt registers authoritative, company registers fallback",
        RegistryQueryScope.None => "not consulted (no register holds this legal form)",
        _ => "all"
    };
}
