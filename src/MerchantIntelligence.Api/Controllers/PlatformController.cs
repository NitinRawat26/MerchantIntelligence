using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.ModelOps;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Scoring;
using MerchantIntelligence.Platform.Webhooks;
using Microsoft.AspNetCore.Mvc;

namespace MerchantIntelligence.Api.Controllers;

public sealed class UnifiedScoreRequest
{
    /// <summary>Optional application; when present the champion model is scored and logged.</summary>
    public CreditDecisionRequest? Application { get; set; }
    public RiskTier? KybRisk { get; set; }
    public bool? BusinessVerified { get; set; }
    [Range(0, 1200)] public int? EntityAgeMonths { get; set; }
    public bool? SanctionsMatch { get; set; }
    public bool? PepMatch { get; set; }
    public bool? AdverseMedia { get; set; }
    public BusinessPolicy? ProhibitedVerdict { get; set; }
    [Range(0, 100)] public int? WebsiteComplianceScore { get; set; }
    [Range(0, 100)] public int? VolumePlausibilityScore { get; set; }
    [RegularExpression("^[A-E]$")] public string? TermsRiskBand { get; set; }
    public bool? MatchFound { get; set; }
    public List<RiskSignal>? Signals { get; set; }
    /// <summary>Extra facts exposed to the rules engine (e.g. "country": "GB").</summary>
    public Dictionary<string, JsonElement>? ExtraFacts { get; set; }
    /// <summary>When true a review case is created from the result.</summary>
    public bool CreateCase { get; set; }
    public string? MerchantName { get; set; }
    public string? ExternalRef { get; set; }
    public string Actor { get; set; } = "api";
}

public sealed record UnifiedScoreResponse(UnifiedRiskScore Score, RulesEvaluation Rules, DecisionResult? CreditDecision, long? DecisionLogId, MerchantCase? Case);

public sealed class RulesEvaluateRequest
{
    [Required] public Dictionary<string, JsonElement> Facts { get; set; } = new();
    public RuleSet? RuleSet { get; set; }
}

public sealed class PublishRulesRequest
{
    [Required] public RuleSet RuleSet { get; set; } = new();
    [Required, MinLength(1)] public string Author { get; set; } = string.Empty;
    public string? Comment { get; set; }
}

public sealed class CreateCaseRequest
{
    [Required, MinLength(1)] public string MerchantName { get; set; } = string.Empty;
    public string? ExternalRef { get; set; }
    public CasePriority? Priority { get; set; }
    [Range(0, 1000)] public int? RiskScore { get; set; }
    public string? RiskTier { get; set; }
    public RuleOutcome? RulesOutcome { get; set; }
    public JsonElement? Snapshot { get; set; }
    public string Actor { get; set; } = "api";
}

public class ActorRequest
{
    [Required, MinLength(1)] public string Actor { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public sealed class AssignRequest : ActorRequest
{
    [Required, MinLength(1)] public string Assignee { get; set; } = string.Empty;
}

public sealed class StatusRequest : ActorRequest
{
    [Required] public CaseStatus Status { get; set; }
}

public sealed class NoteRequest
{
    [Required, MinLength(1)] public string Author { get; set; } = string.Empty;
    [Required, MinLength(1)] public string Body { get; set; } = string.Empty;
}

public sealed class DecideRequest
{
    [Required, RegularExpression("^(Approved|Declined)$")] public string Decision { get; set; } = string.Empty;
    [Required, MinLength(1)] public string Actor { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class RegisterWebhookRequest
{
    [Required, Url] public string Url { get; set; } = string.Empty;
    [Required, MinLength(16)] public string Secret { get; set; } = string.Empty;
    [Required, MinLength(1)] public List<string> Events { get; set; } = new();
}

public sealed class OutcomeRequest
{
    [Required] public Decision Actual { get; set; }
    [Required, MinLength(1)] public string Actor { get; set; } = string.Empty;
}

public sealed class RetrainRequest
{
    [Required, MinLength(1)] public string Actor { get; set; } = string.Empty;
    [Range(0, 200_000)] public int SyntheticRows { get; set; } = 20_000;
    public bool RegisterAsChallenger { get; set; } = true;
}

public sealed class PromoteRequest
{
    [Required, MinLength(1)] public string Actor { get; set; } = string.Empty;
    public string? Justification { get; set; }
}

public sealed class MatchInquiryRequest
{
    [Required, MinLength(1)] public string LegalName { get; set; } = string.Empty;
    public string? DoingBusinessAs { get; set; }
    public string? TaxId { get; set; }
    public string? Country { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public List<MatchPrincipal> Principals { get; set; } = new();
}

[ApiController]
[Route("api/platform")]
public sealed class PlatformController : ControllerBase
{
    private readonly UnifiedRiskScorer _scorer;
    private readonly RulesEngine _rules;
    private readonly RuleSetRepository _ruleSets;
    private readonly CaseService _cases;
    private readonly AuditTrail _audit;
    private readonly WebhookDispatcher _webhooks;
    private readonly ModelRegistry _registry;
    private readonly ModelOpsService _modelOps;
    private readonly IMatchProvider _match;

    public PlatformController(UnifiedRiskScorer scorer, RulesEngine rules, RuleSetRepository ruleSets, CaseService cases,
        AuditTrail audit, WebhookDispatcher webhooks, ModelRegistry registry, ModelOpsService modelOps, IMatchProvider match)
    {
        _scorer = scorer;
        _rules = rules;
        _ruleSets = ruleSets;
        _cases = cases;
        _audit = audit;
        _webhooks = webhooks;
        _registry = registry;
        _modelOps = modelOps;
        _match = match;
    }

    // ---- unified score ----

    [HttpPost("score")]
    public ActionResult<UnifiedScoreResponse> Score([FromBody] UnifiedScoreRequest request)
    {
        MerchantApplication? app = null;
        DecisionResult? credit = null;
        long? logId = null;
        if (request.Application is { } a)
        {
            if (a.HighestTicket < a.AverageTicket)
                return ValidationProblem("highestTicket must be >= averageTicket.");
            app = new MerchantApplication
            {
                MerchantCategoryCode = a.MerchantCategoryCode, AnnualVolume = (float)a.AnnualVolume, AverageTicket = (float)a.AverageTicket,
                HighestTicket = (float)a.HighestTicket, MatchFound = a.MatchFound, ExistingRelationship = a.ExistingRelationship
            };
            (credit, logId) = _modelOps.PredictAndLog(app);
        }

        var input = new UnifiedRiskInput(app, credit, request.KybRisk, request.BusinessVerified, request.EntityAgeMonths, request.SanctionsMatch,
            request.PepMatch, request.AdverseMedia, request.ProhibitedVerdict, request.WebsiteComplianceScore, request.VolumePlausibilityScore,
            request.TermsRiskBand, request.MatchFound ?? app?.MatchFound, request.Signals);
        var score = _scorer.Score(input);
        var facts = RulesEngine.BuildFacts(score, input, request.ExtraFacts?.ToDictionary(k => k.Key, k => Unwrap(k.Value)));
        var evaluation = _rules.Evaluate(_ruleSets.Active, facts);

        MerchantCase? merchantCase = null;
        if (request.CreateCase)
        {
            if (string.IsNullOrWhiteSpace(request.MerchantName)) return ValidationProblem("merchantName is required when createCase is true.");
            merchantCase = _cases.Create(request.MerchantName, request.Actor, request.ExternalRef, null, score.Score, score.Tier, evaluation.Outcome,
                new { score, rules = evaluation, credit, decisionLogId = logId });
        }
        return Ok(new UnifiedScoreResponse(score, evaluation, credit, logId, merchantCase));
    }

    // ---- rules ----

    [HttpGet("rules")]
    public ActionResult<RuleSet> ActiveRules() => Ok(_ruleSets.Active);

    [HttpGet("rules/history")]
    public ActionResult<IReadOnlyList<RuleSetVersion>> RulesHistory() => Ok(_ruleSets.History());

    [HttpGet("rules/{version:int}")]
    public ActionResult<RuleSet> RulesVersion(int version) =>
        _ruleSets.GetVersion(version) is { } set ? Ok(set) : NotFound();

    [HttpPost("rules/validate")]
    public IActionResult ValidateRules([FromBody] RuleSet set)
    {
        try { RulesEngine.Validate(set); return Ok(new { valid = true, rules = set.Rules.Count }); }
        catch (RuleValidationException ex) { return ValidationProblem(ex.Message); }
    }

    [HttpPost("rules/publish")]
    public ActionResult<RuleSetVersion> PublishRules([FromBody] PublishRulesRequest request)
    {
        try { return Ok(_ruleSets.Publish(request.RuleSet, request.Author, request.Comment)); }
        catch (RuleValidationException ex) { return ValidationProblem(ex.Message); }
    }

    [HttpPost("rules/rollback/{version:int}")]
    public ActionResult<RuleSetVersion> RollbackRules(int version, [FromBody] ActorRequest request)
    {
        try { return Ok(_ruleSets.Rollback(version, request.Actor)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("rules/evaluate")]
    public ActionResult<RulesEvaluation> EvaluateRules([FromBody] RulesEvaluateRequest request)
    {
        var set = request.RuleSet ?? _ruleSets.Active;
        try { RulesEngine.Validate(set); }
        catch (RuleValidationException ex) { return ValidationProblem(ex.Message); }
        var facts = request.Facts.ToDictionary(k => k.Key, k => Unwrap(k.Value));
        return Ok(_rules.Evaluate(set, facts));
    }

    // ---- cases ----

    [HttpPost("cases")]
    public ActionResult<MerchantCase> CreateCase([FromBody] CreateCaseRequest request) =>
        Ok(_cases.Create(request.MerchantName, request.Actor, request.ExternalRef, request.Priority, request.RiskScore, request.RiskTier,
            request.RulesOutcome, request.Snapshot));

    [HttpGet("cases")]
    public ActionResult<IReadOnlyList<MerchantCase>> ListCases([FromQuery] CaseStatus? status, [FromQuery] string? assignedTo, [FromQuery] int limit = 100) =>
        Ok(_cases.List(status, assignedTo, Math.Clamp(limit, 1, 500)));

    [HttpGet("cases/stats")]
    public ActionResult<CaseQueueStats> CaseStats() => Ok(_cases.Stats());

    [HttpGet("cases/{id}")]
    public ActionResult<MerchantCase> GetCase(string id) => CaseAction(() => _cases.Get(id));

    [HttpPost("cases/{id}/assign")]
    public ActionResult<MerchantCase> Assign(string id, [FromBody] AssignRequest request) =>
        CaseAction(() => _cases.Assign(id, request.Assignee, request.Actor));

    [HttpPost("cases/{id}/status")]
    public ActionResult<MerchantCase> SetStatus(string id, [FromBody] StatusRequest request) =>
        CaseAction(() => _cases.SetStatus(id, request.Status, request.Actor, request.Reason));

    [HttpPost("cases/{id}/notes")]
    public ActionResult<CaseNote> AddNote(string id, [FromBody] NoteRequest request) =>
        CaseAction(() => _cases.AddNote(id, request.Author, request.Body));

    [HttpGet("cases/{id}/notes")]
    public ActionResult<IReadOnlyList<CaseNote>> Notes(string id) => CaseAction(() => _cases.Notes(id));

    [HttpPost("cases/{id}/decide")]
    public ActionResult<MerchantCase> Decide(string id, [FromBody] DecideRequest request) =>
        CaseAction(() => _cases.Decide(id, new CaseDecision(request.Decision, request.Actor, request.Reason, false)));

    [HttpGet("cases/{id}/audit")]
    public ActionResult<IReadOnlyList<AuditEvent>> CaseAudit(string id) => CaseAction(() => { _cases.Get(id); return _audit.ForCase(id); });

    // ---- audit ----

    [HttpGet("audit")]
    public ActionResult<IReadOnlyList<AuditEvent>> RecentAudit([FromQuery] int limit = 100) => Ok(_audit.Recent(Math.Clamp(limit, 1, 1000)));

    [HttpGet("audit/verify")]
    public ActionResult<AuditVerification> VerifyAudit() => Ok(_audit.Verify());

    // ---- webhooks ----

    [HttpPost("webhooks")]
    public ActionResult<WebhookSubscription> RegisterWebhook([FromBody] RegisterWebhookRequest request)
    {
        try { return Ok(_webhooks.Register(request.Url, request.Secret, request.Events)); }
        catch (ArgumentException ex) { return ValidationProblem(ex.Message); }
    }

    [HttpGet("webhooks")]
    public ActionResult<IReadOnlyList<WebhookSubscription>> ListWebhooks() => Ok(_webhooks.List());

    [HttpDelete("webhooks/{id}")]
    public IActionResult RemoveWebhook(string id) => _webhooks.Remove(id) ? NoContent() : NotFound();

    [HttpGet("webhooks/deliveries")]
    public ActionResult<IReadOnlyList<WebhookDelivery>> Deliveries([FromQuery] string? webhookId, [FromQuery] int limit = 100) =>
        Ok(_webhooks.Deliveries(webhookId, Math.Clamp(limit, 1, 500)));

    [HttpGet("webhooks/events")]
    public ActionResult<IReadOnlyList<string>> WebhookEvents() => Ok(WebhookDispatcher.KnownEvents);

    // ---- model ops ----

    [HttpGet("models")]
    public IActionResult Models() => Ok(new { champion = _registry.ChampionVersion, challenger = _registry.ChallengerVersion, registry = _registry.All() });

    [HttpGet("models/decisions")]
    public ActionResult<IReadOnlyList<LoggedDecision>> Decisions([FromQuery] int limit = 100, [FromQuery] bool onlyLabelled = false) =>
        Ok(_modelOps.Recent(Math.Clamp(limit, 1, 1000), onlyLabelled));

    [HttpPost("models/decisions/{id:long}/outcome")]
    public IActionResult RecordOutcome(long id, [FromBody] OutcomeRequest request) =>
        _modelOps.RecordOutcome(id, request.Actual, request.Actor) ? Ok(new { id, request.Actual }) : NotFound();

    [HttpGet("models/drift")]
    public ActionResult<DriftReport> Drift([FromQuery] DateTimeOffset? since) => Ok(_modelOps.Drift(since));

    [HttpGet("models/compare")]
    public ActionResult<ChampionChallengerReport> Compare() => Ok(_modelOps.Compare());

    [HttpPost("models/retrain")]
    public ActionResult<RetrainResult> Retrain([FromBody] RetrainRequest request) =>
        Ok(_modelOps.Retrain(request.Actor, request.SyntheticRows, request.RegisterAsChallenger));

    [HttpPost("models/promote")]
    public ActionResult<RegisteredModel> Promote([FromBody] PromoteRequest request)
    {
        try { return Ok(_registry.PromoteChallenger(request.Actor, request.Justification)); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // ---- MATCH boundary ----

    [HttpPost("match/inquiry")]
    public async Task<ActionResult<MatchResult>> MatchInquiry([FromBody] MatchInquiryRequest request, CancellationToken ct) =>
        Ok(await _match.InquireAsync(new MatchInquiry(request.LegalName, request.DoingBusinessAs, request.TaxId, request.Country,
            request.AddressLine, request.City, request.Region, request.PostalCode, request.Principals), ct));

    private ActionResult<T> CaseAction<T>(Func<T> action)
    {
        try { return Ok(action()); }
        catch (CaseNotFoundException) { return NotFound(); }
        catch (InvalidCaseTransitionException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return ValidationProblem(ex.Message); }
    }

    private static object? Unwrap(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => e.EnumerateArray().Select(Unwrap).ToList(),
        _ => e.GetRawText()
    };
}
