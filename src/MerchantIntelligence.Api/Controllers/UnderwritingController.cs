using System.ComponentModel.DataAnnotations;
using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Underwriting.Explainability;
using MerchantIntelligence.Underwriting.Financials;
using MerchantIntelligence.Underwriting.Plausibility;
using MerchantIntelligence.Underwriting.Pricing;
using Microsoft.AspNetCore.Mvc;

namespace MerchantIntelligence.Api.Controllers;

public sealed class ExplainRequest : CreditDecisionRequest
{
    /// <summary>Class to explain; defaults to the predicted decision.</summary>
    public Decision? ExplainClass { get; set; }
}

public sealed class TermsRequest : CreditDecisionRequest
{
    [Range(0, 365)] public int? DeliveryDays { get; set; }
    [Range(0.0, 1.0)] public double CardNotPresentShare { get; set; } = 1.0;
    public bool? KybHighRisk { get; set; }
    [Range(0, 100)] public int? WebsiteComplianceScore { get; set; }
    [Range(0, 100)] public int? VolumePlausibilityScore { get; set; }
    public bool OffersSubscriptions { get; set; }
    public bool OffersFreeTrials { get; set; }
}

public sealed class VolumePlausibilityRequest
{
    [Range(0, double.MaxValue)] public decimal AnnualVolume { get; set; }
    [Range(0.01, double.MaxValue)] public decimal AverageTicket { get; set; }
    [Range(0, double.MaxValue)] public decimal? HighestTicket { get; set; }
    [Range(1, 9999)] public int? MerchantCategoryCode { get; set; }
    [Range(0, 1_000_000)] public int? EmployeeCount { get; set; }
    [Range(0, 100_000)] public int? LocationCount { get; set; }
    [Range(0, 200)] public decimal? YearsInBusiness { get; set; }
    [Range(0, double.MaxValue)] public decimal? PriorYearRevenue { get; set; }
    [Range(0, double.MaxValue)] public decimal? MonthlyCardVolumeFromStatements { get; set; }
    [Range(0, int.MaxValue)] public int? WebsiteProductCount { get; set; }
    public bool? HasPhysicalLocation { get; set; }
}

public sealed class BankStatementTextRequest
{
    [Required] public string Csv { get; set; } = string.Empty;
}

public sealed class FinancialStatementTextRequest
{
    [Required] public string Text { get; set; } = string.Empty;
    public decimal? DeclaredAnnualCardVolume { get; set; }
}

[ApiController]
[Route("api/underwriting")]
public sealed class UnderwritingController(
    DecisionExplainer explainer,
    ReservePricingRecommender recommender,
    VolumePlausibilityAnalyzer plausibility) : ControllerBase
{
    private const long MaxUpload = 20 * 1024 * 1024;

    [HttpPost("explain")]
    [ProducesResponseType<DecisionExplanation>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<DecisionExplanation> Explain([FromBody] ExplainRequest request)
    {
        if (!ValidateTickets(request)) return ValidationProblem(ModelState);
        return Ok(explainer.Explain(ToApplication(request), request.ExplainClass));
    }

    [HttpPost("recommend-terms")]
    [ProducesResponseType<TermsRecommendation>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<TermsRecommendation> RecommendTerms([FromBody] TermsRequest request)
    {
        if (!ValidateTickets(request)) return ValidationProblem(ModelState);
        return Ok(recommender.Recommend(new PricingInput(ToApplication(request), request.DeliveryDays, request.CardNotPresentShare,
            request.KybHighRisk, request.WebsiteComplianceScore, request.VolumePlausibilityScore, request.OffersSubscriptions, request.OffersFreeTrials)));
    }

    [HttpPost("volume-plausibility")]
    [ProducesResponseType<VolumePlausibilityResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<VolumePlausibilityResult> VolumePlausibility([FromBody] VolumePlausibilityRequest r) =>
        Ok(plausibility.Analyze(new VolumeDeclaration(r.AnnualVolume, r.AverageTicket, r.HighestTicket, r.MerchantCategoryCode, r.EmployeeCount,
            r.YearsInBusiness, r.PriorYearRevenue, r.MonthlyCardVolumeFromStatements, r.WebsiteProductCount, r.HasPhysicalLocation, r.LocationCount)));

    /// <summary>Upload a bank statement (CSV or text-based PDF) as multipart/form-data field "file".</summary>
    [HttpPost("bank-statement")]
    [RequestSizeLimit(MaxUpload)]
    [ProducesResponseType<CashFlowAnalysis>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CashFlowAnalysis>> BankStatement(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) return Problem("Empty file.", statusCode: StatusCodes.Status400BadRequest);
        await using var stream = await BufferAsync(file, ct);
        return Analyse(() => CashFlowAnalyzer.Analyze(BankStatementParser.Parse(stream, file.FileName)));
    }

    /// <summary>Analyse bank-statement CSV supplied inline (for clients that cannot do multipart).</summary>
    [HttpPost("bank-statement/csv")]
    [ProducesResponseType<CashFlowAnalysis>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<CashFlowAnalysis> BankStatementCsv([FromBody] BankStatementTextRequest request) =>
        Analyse(() => CashFlowAnalyzer.Analyze(BankStatementParser.ParseCsv(request.Csv)));

    /// <summary>Upload a P&amp;L / balance sheet (CSV, text or text-based PDF) as multipart/form-data field "file".</summary>
    [HttpPost("financial-statement")]
    [RequestSizeLimit(MaxUpload)]
    [ProducesResponseType<FinancialStatementAnalysis>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FinancialStatementAnalysis>> FinancialStatement(IFormFile file, [FromForm] decimal? declaredAnnualCardVolume, CancellationToken ct)
    {
        if (file.Length == 0) return Problem("Empty file.", statusCode: StatusCodes.Status400BadRequest);
        await using var stream = await BufferAsync(file, ct);
        return Analyse(() => ProfitAndLossAnalyzer.Analyze(stream, file.FileName, declaredAnnualCardVolume));
    }

    [HttpPost("financial-statement/text")]
    [ProducesResponseType<FinancialStatementAnalysis>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<FinancialStatementAnalysis> FinancialStatementText([FromBody] FinancialStatementTextRequest request) =>
        Analyse(() => ProfitAndLossAnalyzer.AnalyzeText(request.Text, request.DeclaredAnnualCardVolume));

    private ActionResult<T> Analyse<T>(Func<T> run)
    {
        try
        {
            return Ok(run());
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException || ex.GetType().Namespace?.StartsWith("UglyToad", StringComparison.Ordinal) == true)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Could not parse the supplied statement.");
        }
    }

    private static async Task<MemoryStream> BufferAsync(IFormFile file, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    private bool ValidateTickets(CreditDecisionRequest request)
    {
        if (request.HighestTicket < request.AverageTicket)
            ModelState.AddModelError(nameof(request.HighestTicket), "HighestTicket must be >= AverageTicket.");
        return ModelState.IsValid;
    }

    private static MerchantApplication ToApplication(CreditDecisionRequest r) => new()
    {
        MerchantCategoryCode = r.MerchantCategoryCode,
        AnnualVolume = (float)r.AnnualVolume,
        AverageTicket = (float)r.AverageTicket,
        HighestTicket = (float)r.HighestTicket,
        MatchFound = r.MatchFound,
        ExistingRelationship = r.ExistingRelationship
    };
}
