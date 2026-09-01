using System.ComponentModel.DataAnnotations;
using MerchantIntelligence.CreditDecision;
using Microsoft.AspNetCore.Mvc;

namespace MerchantIntelligence.Api.Controllers;

public sealed class CreditDecisionRequest
{
    [Range(1, 9999)] public int MerchantCategoryCode { get; set; }
    [Range(0, double.MaxValue)] public decimal AnnualVolume { get; set; }
    [Range(0, double.MaxValue)] public decimal AverageTicket { get; set; }
    [Range(0, double.MaxValue)] public decimal HighestTicket { get; set; }
    public bool MatchFound { get; set; }
    public bool ExistingRelationship { get; set; }
}

public sealed record CreditDecisionResponse(
    Decision Decision,
    double ConfidencePercent,
    IReadOnlyDictionary<Decision, double> ProbabilitiesPercent);

[ApiController]
[Route("api/credit-decision")]
public sealed class CreditDecisionController(IDecisionPredictor predictor) : ControllerBase
{
    [HttpPost("predict")]
    [ProducesResponseType<CreditDecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<CreditDecisionResponse> Predict([FromBody] CreditDecisionRequest request)
    {
        if (request.HighestTicket < request.AverageTicket)
        {
            ModelState.AddModelError(nameof(request.HighestTicket), "HighestTicket must be >= AverageTicket.");
            return ValidationProblem(ModelState);
        }

        var result = predictor.Predict(new MerchantApplication
        {
            MerchantCategoryCode = request.MerchantCategoryCode,
            AnnualVolume = (float)request.AnnualVolume,
            AverageTicket = (float)request.AverageTicket,
            HighestTicket = (float)request.HighestTicket,
            MatchFound = request.MatchFound,
            ExistingRelationship = request.ExistingRelationship
        });

        return Ok(new CreditDecisionResponse(
            result.Decision,
            ToPercent(result.Confidence),
            result.Probabilities.ToDictionary(kv => kv.Key, kv => ToPercent(kv.Value))));
    }

    private static double ToPercent(double p) => Math.Round(p * 100, 2);
}
