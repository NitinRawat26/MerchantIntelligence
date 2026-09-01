using System.ComponentModel.DataAnnotations;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using Microsoft.AspNetCore.Mvc;

namespace MerchantIntelligence.Api.Controllers;

public sealed class MccValidationRequest
{
    [Range(1, 9999)] public int Mcc { get; set; }
    [Required] public string WebsiteUrl { get; set; } = string.Empty;
}

public sealed record MccCatalogItem(int Mcc, string Description, string Category, RiskTier RiskTier);

[ApiController]
[Route("api/mcc-validation")]
public sealed class MccValidationController(MccValidationService service, MccCatalog catalog) : ControllerBase
{
    [HttpPost("validate")]
    [ProducesResponseType<MccValidationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MccValidationResult>> Validate([FromBody] MccValidationRequest request, CancellationToken ct)
    {
        var raw = request.WebsiteUrl.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || url.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || !url.Host.Contains('.'))
        {
            ModelState.AddModelError(nameof(request.WebsiteUrl), "WebsiteUrl must be a valid http(s) URL.");
            return ValidationProblem(ModelState);
        }

        var result = await service.ValidateAsync(request.Mcc, url, ct);
        return Ok(result);
    }

    [HttpGet("catalog")]
    [ProducesResponseType<IReadOnlyList<MccCatalogItem>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MccCatalogItem>> Catalog() =>
        Ok(catalog.Entries
            .OrderBy(e => e.Mcc)
            .Select(e => new MccCatalogItem(e.Mcc, e.Description, e.Category, e.RiskTier))
            .ToList());
}
