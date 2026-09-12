using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using MerchantIntelligence.Platform.Assessment;
using Microsoft.AspNetCore.Mvc;

namespace MerchantIntelligence.Api.Controllers;

/// <summary>Single intake covering every check in the suite.</summary>
public sealed class AssessmentRequest
{
    [Required] public BusinessIdentityRequest Business { get; set; } = new();
    public List<BeneficialOwnerRequest> Owners { get; set; } = new();
    public string? BusinessDescription { get; set; }
    [Range(1, 9999)] public int MerchantCategoryCode { get; set; }
    [Range(0, double.MaxValue)] public decimal AnnualVolume { get; set; }
    [Range(0.01, double.MaxValue)] public decimal AverageTicket { get; set; }
    [Range(0, double.MaxValue)] public decimal HighestTicket { get; set; }
    public bool ExistingRelationship { get; set; }
    [Range(0, 365)] public int? DeliveryDays { get; set; }
    [Range(0.0, 1.0)] public double CardNotPresentShare { get; set; } = 1.0;
    public bool OffersSubscriptions { get; set; }
    public bool OffersFreeTrials { get; set; }
    [Range(0, 1_000_000)] public int? EmployeeCount { get; set; }
    [Range(0, 100_000)] public int? LocationCount { get; set; }
    [Range(0, 200)] public decimal? YearsInBusiness { get; set; }
    [Range(0, double.MaxValue)] public decimal? PriorYearRevenue { get; set; }
    [Range(0, int.MaxValue)] public int? WebsiteProductCount { get; set; }
    public bool? HasPhysicalLocation { get; set; }
    /// <summary>Inline bank-statement CSV; alternatively upload a file in the multipart field "bankStatement".</summary>
    public string? BankStatementCsv { get; set; }
    /// <summary>Inline P&amp;L / balance-sheet text; alternatively upload a file in the multipart field "financialStatement".</summary>
    public string? FinancialStatementText { get; set; }
    public string? ExternalRef { get; set; }
    [MinLength(1)] public string Actor { get; set; } = "analyst";
    public bool CreateCase { get; set; } = true;

    public AssessmentIntake ToIntake() => new(Business.ToIdentity(), Owners.Select(o => o.ToOwner()).ToList(), BusinessDescription, MerchantCategoryCode,
        AnnualVolume, AverageTicket, HighestTicket, ExistingRelationship, DeliveryDays, CardNotPresentShare, OffersSubscriptions, OffersFreeTrials,
        EmployeeCount, YearsInBusiness, PriorYearRevenue, WebsiteProductCount, HasPhysicalLocation, BankStatementCsv, FinancialStatementText,
        ExternalRef, Actor, CreateCase, LocationCount);
}

[ApiController]
[Route("api/assessment")]
public sealed class AssessmentController(AssessmentService assessments) : ControllerBase
{
    private const long MaxUpload = 20 * 1024 * 1024;

    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The ordered list of checks the active workflow runs, for rendering progress before the first event arrives.</summary>
    [HttpGet("steps")]
    public ActionResult<IReadOnlyList<AssessmentStepDescriptor>> Steps() => Ok(assessments.PlannedSteps());

    /// <summary>The agents of the active workflow and the checks each one owns.</summary>
    [HttpGet("agents")]
    public ActionResult<IReadOnlyList<AssessmentAgentDescriptor>> Agents() => Ok(assessments.PlannedAgents());

    /// <summary>
    /// Run every check and return the complete assessment. JSON body, or multipart/form-data with a "request" JSON part
    /// plus optional "bankStatement" and "financialStatement" files (CSV / text / text-based PDF).
    /// </summary>
    [HttpPost("run")]
    [RequestSizeLimit(MaxUpload)]
    [ProducesResponseType<AssessmentResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        var (request, bank, fin, problem) = await ReadRequestAsync(ct);
        if (problem is not null) return problem;
        var result = await assessments.RunAsync(request!.ToIntake(), bank, fin, null, ct);
        return Ok(result);
    }

    /// <summary>
    /// Same as <c>run</c> but streams newline-delimited JSON: one <c>{"type":"step",...}</c> line per check as it starts
    /// and finishes, then a final <c>{"type":"result",...}</c> line.
    /// </summary>
    [HttpPost("run/stream")]
    [RequestSizeLimit(MaxUpload)]
    [Produces("application/x-ndjson")]
    public async Task RunStream(CancellationToken ct)
    {
        var (request, bank, fin, problem) = await ReadRequestAsync(ct);
        if (problem is not null)
        {
            await problem.ExecuteResultAsync(ControllerContext);
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        var body = Response.Body;
        var gate = new SemaphoreSlim(1, 1);

        async Task Emit(object payload)
        {
            await gate.WaitAsync(ct);
            try
            {
                await JsonSerializer.SerializeAsync(body, payload, StreamJson, ct);
                await body.WriteAsync("\n"u8.ToArray(), ct);
                await body.FlushAsync(ct);
            }
            finally { gate.Release(); }
        }

        await Emit(new { type = "steps", steps = assessments.PlannedSteps(), agents = assessments.PlannedAgents() });
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                while (await timer.WaitForNextTickAsync(heartbeatCts.Token))
                    await Emit(new { type = "heartbeat", at = DateTimeOffset.UtcNow });
            }
            catch (OperationCanceledException) { }
        });
        async Task StopHeartbeat() { heartbeatCts.Cancel(); await heartbeat; }
        try
        {
            var result = await assessments.RunAsync(request!.ToIntake(), bank, fin, step => Emit(new { type = "step", step }), ct,
                agentProgress: agent => Emit(new { type = "agent", agent }));
            await StopHeartbeat();
            await Emit(new { type = "result", result });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await StopHeartbeat(); }
        catch (Exception ex)
        {
            await StopHeartbeat();
            await Emit(new { type = "error", error = ex.Message });
        }
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<AssessmentListItem>> List([FromQuery] int limit = 50) => Ok(assessments.List(Math.Clamp(limit, 1, 500)));

    [HttpGet("{id}")]
    [ProducesResponseType<AssessmentResult>(StatusCodes.Status200OK)]
    public ActionResult<AssessmentResult> Get(string id) => assessments.Get(id) is { } r ? Ok(r) : NotFound();

    /// <summary>Printable underwriting memo for a completed assessment.</summary>
    [HttpGet("{id}/pdf")]
    [Produces("application/pdf")]
    public IActionResult Pdf(string id)
    {
        var result = assessments.Get(id);
        if (result is null) return NotFound();
        var bytes = AssessmentPdfRenderer.Render(result);
        var safeName = string.Concat(result.Intake.Business.LegalName.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Trim().Replace(' ', '-');
        return File(bytes, "application/pdf", $"assessment-{safeName}-{id}.pdf");
    }

    private async Task<(AssessmentRequest? Request, UploadedDocument? Bank, UploadedDocument? Financial, IActionResult? Problem)> ReadRequestAsync(CancellationToken ct)
    {
        AssessmentRequest? request;
        UploadedDocument? bank = null;
        UploadedDocument? fin = null;

        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            var json = form["request"].ToString();
            if (string.IsNullOrWhiteSpace(json)) return (null, null, null, ValidationProblem("Multipart field 'request' (JSON) is required."));
            try { request = JsonSerializer.Deserialize<AssessmentRequest>(json, StreamJson); }
            catch (JsonException ex) { return (null, null, null, ValidationProblem($"Invalid 'request' JSON: {ex.Message}")); }
            bank = await ReadFileAsync(form.Files["bankStatement"], ct);
            fin = await ReadFileAsync(form.Files["financialStatement"], ct);
        }
        else
        {
            try { request = await JsonSerializer.DeserializeAsync<AssessmentRequest>(Request.Body, StreamJson, ct); }
            catch (JsonException ex) { return (null, null, null, ValidationProblem($"Invalid JSON body: {ex.Message}")); }
        }

        if (request is null) return (null, null, null, ValidationProblem("Request body is required."));
        if (!TryValidateModel(request)) return (null, null, null, ValidationProblem(ModelState));
        if (request.HighestTicket < request.AverageTicket) return (null, null, null, ValidationProblem("HighestTicket must be >= AverageTicket."));
        if (!string.IsNullOrWhiteSpace(request.Business.WebsiteUrl) && !KybController.TryParseUrl(request.Business.WebsiteUrl, out _))
            return (null, null, null, ValidationProblem("Business.WebsiteUrl must be a valid http(s) URL."));
        return (request, bank, fin, null);
    }

    private static async Task<UploadedDocument?> ReadFileAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return null;
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return new UploadedDocument(file.FileName, ms.ToArray());
    }
}
