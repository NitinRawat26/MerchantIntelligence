using System.ComponentModel.DataAnnotations;
using MerchantIntelligence.Kyb;
using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using Microsoft.AspNetCore.Mvc;

namespace MerchantIntelligence.Api.Controllers;

public sealed class BusinessIdentityRequest
{
    [Required, MinLength(2)] public string LegalName { get; set; } = string.Empty;
    public string? TradingName { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? TaxId { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? WebsiteUrl { get; set; }

    public BusinessIdentity ToIdentity() => new(LegalName.Trim(), TradingName, RegistrationNumber, TaxId, AddressLine, City, Region, PostalCode, Country, WebsiteUrl);
}

public sealed class BeneficialOwnerRequest
{
    [Required, MinLength(2)] public string FullName { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }
    public string? Nationality { get; set; }
    public string? Role { get; set; }
    [Range(0, 100)] public double? OwnershipPercent { get; set; }

    public BeneficialOwner ToOwner() => new(FullName.Trim(), DateOfBirth, Nationality, Role, OwnershipPercent);
}

public sealed class ScreeningRequest
{
    [Required, MinLength(1)] public List<ScreeningSubjectRequest> Subjects { get; set; } = new();
}

public sealed class ScreeningSubjectRequest
{
    [Required, MinLength(2)] public string Name { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }
    public string? Country { get; set; }
    public bool IsIndividual { get; set; }
    public string? Role { get; set; }
}

public sealed class WebsiteComplianceRequest
{
    [Required] public string WebsiteUrl { get; set; } = string.Empty;
    public string? BusinessDescription { get; set; }
    [Range(1, 9999)] public int? DeclaredMcc { get; set; }
    public string? LegalName { get; set; }
}

public sealed class ProhibitedBusinessRequest
{
    public string? Text { get; set; }
    public string? BusinessDescription { get; set; }
    [Range(1, 9999)] public int? DeclaredMcc { get; set; }
}

public sealed class FullKybRequest
{
    [Required] public BusinessIdentityRequest Business { get; set; } = new();
    public List<BeneficialOwnerRequest> Owners { get; set; } = new();
    public string? BusinessDescription { get; set; }
    [Range(1, 9999)] public int? DeclaredMcc { get; set; }
}

[ApiController]
[Route("api/kyb")]
public sealed class KybController(
    BusinessVerificationService verification,
    SanctionsScreeningService screening,
    WebsiteComplianceScanner websiteScanner,
    ProhibitedBusinessDetector prohibited,
    KybReportService reportService) : ControllerBase
{
    /// <summary>Match a declared legal entity against GLEIF, SEC EDGAR and any configured registries.</summary>
    [HttpPost("verify-business")]
    [ProducesResponseType<BusinessVerificationResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BusinessVerificationResult>> VerifyBusiness([FromBody] BusinessIdentityRequest request, CancellationToken ct) =>
        Ok(await verification.VerifyAsync(request.ToIdentity(), ct));

    /// <summary>Screen people / entities against OFAC, UN, EU (via OpenSanctions) and adverse media.</summary>
    [HttpPost("screen")]
    [ProducesResponseType<ScreeningReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ScreeningReport>> Screen([FromBody] ScreeningRequest request, CancellationToken ct)
    {
        var subjects = request.Subjects.Select(s => new ScreeningSubject(s.Name.Trim(), s.DateOfBirth, s.Country, s.IsIndividual, s.Role)).ToList();
        return Ok(await screening.ScreenAsync(subjects, ct));
    }

    [HttpGet("screen/lists")]
    [ProducesResponseType<IReadOnlyList<SanctionsListStatus>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SanctionsListStatus>>> Lists(CancellationToken ct)
    {
        await screening.GetIndexAsync(ct);
        return Ok(screening.ListStatuses);
    }

    /// <summary>Check a merchant website for card-brand mandated disclosures, TLS, domain age and prohibited content.</summary>
    [HttpPost("website-compliance")]
    [ProducesResponseType<WebsiteComplianceResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WebsiteComplianceResult>> WebsiteCompliance([FromBody] WebsiteComplianceRequest request, CancellationToken ct)
    {
        if (!TryParseUrl(request.WebsiteUrl, out var url))
        {
            ModelState.AddModelError(nameof(request.WebsiteUrl), "WebsiteUrl must be a valid http(s) URL.");
            return ValidationProblem(ModelState);
        }
        return Ok(await websiteScanner.ScanAsync(url, request.BusinessDescription, request.DeclaredMcc, request.LegalName, ct));
    }

    /// <summary>Classify free text / a business description against the prohibited & restricted taxonomy.</summary>
    [HttpPost("prohibited-business")]
    [ProducesResponseType<ProhibitedBusinessResult>(StatusCodes.Status200OK)]
    public ActionResult<ProhibitedBusinessResult> ProhibitedBusiness([FromBody] ProhibitedBusinessRequest request) =>
        Ok(prohibited.Analyze(request.Text, request.BusinessDescription, request.DeclaredMcc));

    [HttpGet("prohibited-business/categories")]
    [ProducesResponseType<IReadOnlyList<RestrictedCategory>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RestrictedCategory>> Categories() => Ok(prohibited.Categories);

    /// <summary>Full KYB pass: verification + UBO screening + website compliance.</summary>
    [HttpPost("report")]
    [ProducesResponseType<KybReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<KybReport>> Report([FromBody] FullKybRequest request, CancellationToken ct)
    {
        var kyb = new KybRequest(request.Business.ToIdentity(), request.Owners.Select(o => o.ToOwner()).ToList(), request.BusinessDescription, request.DeclaredMcc);
        return Ok(await reportService.RunAsync(kyb, ct));
    }

    internal static bool TryParseUrl(string raw, out Uri url)
    {
        raw = raw.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        return Uri.TryCreate(raw, UriKind.Absolute, out url!)
               && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
               && url.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
               && url.Host.Contains('.');
    }
}
