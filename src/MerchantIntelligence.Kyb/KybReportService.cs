using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.Kyb;

public sealed record BeneficialOwner(
    string FullName,
    DateOnly? DateOfBirth = null,
    string? Nationality = null,
    string? Role = null,
    double? OwnershipPercent = null);

public sealed record KybRequest(
    BusinessIdentity Business,
    IReadOnlyList<BeneficialOwner> Owners,
    string? BusinessDescription = null,
    int? DeclaredMcc = null);

public sealed record KybReport(
    BusinessVerificationResult Verification,
    ScreeningReport Screening,
    WebsiteComplianceResult? WebsiteCompliance,
    RiskTier OverallRisk,
    IReadOnlyList<KybFlag> Flags);

/// <summary>Runs entity verification, sanctions/PEP/adverse-media screening and the website scan in one pass.</summary>
public sealed class KybReportService
{
    private readonly BusinessVerificationService _verification;
    private readonly SanctionsScreeningService _screening;
    private readonly WebsiteComplianceScanner _website;

    public KybReportService(BusinessVerificationService verification, SanctionsScreeningService screening, WebsiteComplianceScanner website)
    {
        _verification = verification;
        _screening = screening;
        _website = website;
    }

    public async Task<KybReport> RunAsync(KybRequest request, CancellationToken ct = default)
    {
        var subjects = new List<ScreeningSubject>
        {
            new(request.Business.LegalName, null, request.Business.Country, IsIndividual: false, Role: "Business")
        };
        if (!string.IsNullOrWhiteSpace(request.Business.TradingName) && request.Business.TradingName != request.Business.LegalName)
            subjects.Add(new ScreeningSubject(request.Business.TradingName, null, request.Business.Country, false, "Trading name"));
        subjects.AddRange(request.Owners.Select(o => new ScreeningSubject(o.FullName, o.DateOfBirth, o.Nationality, true, o.Role ?? "Beneficial owner")));

        var verifyTask = _verification.VerifyAsync(request.Business, ct);
        var screenTask = _screening.ScreenAsync(subjects, ct);
        Task<WebsiteComplianceResult>? siteTask = null;
        if (!string.IsNullOrWhiteSpace(request.Business.WebsiteUrl))
        {
            var raw = request.Business.WebsiteUrl.Trim();
            if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
            if (Uri.TryCreate(raw, UriKind.Absolute, out var url))
                siteTask = _website.ScanAsync(url, request.BusinessDescription, request.DeclaredMcc, request.Business.LegalName, ct);
        }

        var verification = await verifyTask;
        var screening = await screenTask;
        var site = siteTask is null ? null : await siteTask;

        var flags = new List<KybFlag>(verification.Flags);
        flags.AddRange(screening.Flags.Select(f => new KybFlag(f.Code, f.Message, f.Severity)));
        if (site is not null)
        {
            flags.AddRange(site.Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => new KybFlag($"WEB_{c.Code}", c.Detail, c.Severity)));
            if (site.Score < 60) flags.Add(new KybFlag("WEBSITE_COMPLIANCE_LOW", $"Website compliance score {site.Score}/100 (grade {site.Grade}).", RiskTier.Medium));
        }

        var ownershipTotal = request.Owners.Sum(o => o.OwnershipPercent ?? 0);
        if (request.Owners.Count == 0)
            flags.Add(new KybFlag("NO_UBO_DECLARED", "No beneficial owners supplied; UBO screening not performed.", RiskTier.Medium));
        else if (ownershipTotal > 0 && ownershipTotal < 75)
            flags.Add(new KybFlag("UBO_COVERAGE_LOW", $"Declared owners cover only {ownershipTotal:F0}% of ownership; identify holders of ≥25%.", RiskTier.Medium));

        var overall = flags.Count == 0 ? RiskTier.Low : flags.Max(f => f.Severity);
        return new KybReport(verification, screening, site, overall, flags);
    }
}
