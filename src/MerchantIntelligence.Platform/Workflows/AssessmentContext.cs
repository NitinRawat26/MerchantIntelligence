using System.Diagnostics;
using System.Text.Json;
using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb;
using MerchantIntelligence.Kyb.Compliance;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Validation;
using MerchantIntelligence.Platform.Assessment;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Integrations;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Scoring;
using MerchantIntelligence.Underwriting.Explainability;
using MerchantIntelligence.Underwriting.Financials;
using MerchantIntelligence.Underwriting.Plausibility;
using MerchantIntelligence.Underwriting.Pricing;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>Thrown by a step whose failure policy is <see cref="StepFailurePolicy.Abort"/>.</summary>
public sealed class StepAbortedException(string stepId, Exception inner) : Exception($"Step '{stepId}' failed and the workflow is configured to abort: {inner.Message}", inner)
{
    public string StepId { get; } = stepId;
}

/// <summary>
/// Mutable state shared by the steps of one assessment run. Each step writes its own result slot; the
/// engine guarantees a step only runs after its dependencies, so reads of other slots are race-free.
/// </summary>
public sealed class AssessmentContext
{
    private readonly List<AssessmentStep> _steps = new();
    private readonly Func<AssessmentStep, Task> _report;
    private readonly AsyncLocal<Func<AssessmentStep, Task>?> _sink = new();
    private readonly ILogger _logger;
    private int _forcedRefer;

    public AssessmentContext(string assessmentId, AssessmentIntake intake, UploadedDocument? bankStatement, UploadedDocument? financialStatement,
        WorkflowDefinition workflow, Func<AssessmentStep, Task> report, ILogger logger, CancellationToken ct)
    {
        AssessmentId = assessmentId;
        Intake = intake;
        BankStatementDocument = bankStatement;
        FinancialStatementDocument = financialStatement;
        Workflow = workflow;
        _report = report;
        _logger = logger;
        CancellationToken = ct;
        StartedAt = DateTimeOffset.UtcNow;
        SiteUrl = ParseUrl(intake.Business.WebsiteUrl);
    }

    public string AssessmentId { get; }
    public DateTimeOffset StartedAt { get; }
    public AssessmentIntake Intake { get; }
    public UploadedDocument? BankStatementDocument { get; }
    public UploadedDocument? FinancialStatementDocument { get; }
    public WorkflowDefinition Workflow { get; }
    public CancellationToken CancellationToken { get; }
    public Uri? SiteUrl { get; }

    // ---- result slots, one per step ----
    public BusinessVerificationResult? Verification { get; set; }
    public ScreeningReport? Screening { get; set; }
    public WebsiteComplianceResult? Website { get; set; }
    public ProhibitedBusinessResult? Prohibited { get; set; }
    public MccValidationResult? Mcc { get; set; }
    public MatchResult? Match { get; set; }
    public CashFlowAnalysis? Bank { get; set; }
    public FinancialStatementAnalysis? Financials { get; set; }
    public VolumePlausibilityResult? Plausibility { get; set; }
    private MerchantApplication? _application;
    /// <summary>Feature vector for the credit model; built on first use so it sees the MATCH result when that step ran first.</summary>
    public MerchantApplication Application => _application ??= new MerchantApplication
    {
        MerchantCategoryCode = Intake.MerchantCategoryCode, AnnualVolume = (float)Intake.AnnualVolume, AverageTicket = (float)Intake.AverageTicket,
        HighestTicket = (float)Intake.HighestTicket, MatchFound = Match?.Found == true, ExistingRelationship = Intake.ExistingRelationship
    };
    public DecisionResult? Credit { get; set; }
    public DecisionExplanation? CreditExplanation { get; set; }
    public long? DecisionLogId { get; set; }
    public TermsRecommendation? Terms { get; set; }
    public RiskTier? KybRisk { get; set; }
    public IReadOnlyList<RiskSignal> Signals { get; set; } = [];
    public UnifiedRiskInput? ScoreInput { get; set; }
    public UnifiedRiskScore? Score { get; set; }
    public RulesEvaluation? Rules { get; set; }
    public AssessmentDecision? Decision { get; set; }
    public AssessmentExplainability? Explainability { get; set; }
    public MerchantCase? Case { get; set; }

    /// <summary>Recorded steps in workflow order (parallel stages finish in arbitrary order).</summary>
    public IReadOnlyList<AssessmentStep> Steps
    {
        get
        {
            var position = Workflow.Steps.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
            lock (_steps) return _steps.OrderBy(s => position.GetValueOrDefault(s.Id, int.MaxValue)).ToList();
        }
    }

    /// <summary>Set when a step failed under the <see cref="StepFailurePolicy.Refer"/> policy.</summary>
    public bool ForcedRefer => Volatile.Read(ref _forcedRefer) == 1;

    /// <summary>A blocking finding already exists, so further evidence gathering cannot change the outcome.</summary>
    public string? HardStop
    {
        get
        {
            if (Screening is not null && AssessmentComposer.ListsLoaded(Screening) && Screening.Flags.Any(f => f.Code == "SANCTIONS_MATCH")) return "SANCTIONS_MATCH";
            if (Prohibited?.Verdict == BusinessPolicy.Prohibited) return "PROHIBITED_BUSINESS";
            if (Match is { Availability: MatchAvailability.Available, Found: true }) return "MATCH_LISTED";
            return null;
        }
    }

    public bool HasBankInput => BankStatementDocument is not null || !string.IsNullOrWhiteSpace(Intake.BankStatementCsv);
    public bool HasFinancialInput => FinancialStatementDocument is not null || !string.IsNullOrWhiteSpace(Intake.FinancialStatementText);

    /// <summary>Reads an operator-supplied step parameter, falling back to the default when absent or malformed.</summary>
    public T Param<T>(string stepId, string name, T fallback)
    {
        var raw = Workflow.Step(stepId)?.Params;
        if (raw is null || !raw.TryGetValue(name, out var el)) return fallback;
        try { return el.Deserialize<T>(RulesEngine.JsonOptions) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    public async Task<T?> RunAsync<T>(WorkflowStepDescriptor step, Func<Task<T>> run, Func<T, string> summarise) where T : class
    {
        await Report(new AssessmentStep(step.Id, step.Name, StepStatus.Running, "Running…", 0));
        var sw = Stopwatch.StartNew();
        try
        {
            var value = await run();
            await Add(new AssessmentStep(step.Id, step.Name, StepStatus.Succeeded, summarise(value), sw.ElapsedMilliseconds));
            return value;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assessment step {Step} failed", step.Id);
            var policy = Workflow.Step(step.Id)?.OnFail ?? StepFailurePolicy.Skip;
            var note = policy switch
            {
                StepFailurePolicy.Refer => "Check failed; treated as not run and the outcome is forced to Refer.",
                StepFailurePolicy.Abort => "Check failed; workflow aborted.",
                _ => "Check failed; treated as not run (coverage gap)."
            };
            await Add(new AssessmentStep(step.Id, step.Name, StepStatus.Failed, note, sw.ElapsedMilliseconds, ex.Message));
            if (policy == StepFailurePolicy.Refer) Interlocked.Exchange(ref _forcedRefer, 1);
            if (policy == StepFailurePolicy.Abort) throw new StepAbortedException(step.Id, ex);
            return null;
        }
    }

    public Task SkipAsync(WorkflowStepDescriptor step, string reason) => Add(new AssessmentStep(step.Id, step.Name, StepStatus.Skipped, reason, 0));

    private async Task Add(AssessmentStep step)
    {
        lock (_steps) _steps.Add(step);
        await Report(step);
    }

    private Task Report(AssessmentStep step) => (_sink.Value ?? _report)(step);

    /// <summary>Delivers a progress update to the caller's callback (the runner uses this for events it drains from the graph).</summary>
    public Task ReportAsync(AssessmentStep step) => _report(step);

    /// <summary>Routes progress raised on the current async flow through <paramref name="sink"/> until disposed.</summary>
    public IDisposable Capture(Func<AssessmentStep, Task> sink)
    {
        _sink.Value = sink;
        return new Release(this);
    }

    private sealed class Release(AssessmentContext owner) : IDisposable
    {
        public void Dispose() => owner._sink.Value = null;
    }

    private static Uri? ParseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        return Uri.TryCreate(raw, UriKind.Absolute, out var url) && url.Host.Contains('.') ? url : null;
    }
}

/// <summary>A pluggable assessment check. Implementations are stateless singletons; all run state lives in the context.</summary>
public interface IAssessmentStep
{
    WorkflowStepDescriptor Descriptor { get; }
    Task ExecuteAsync(AssessmentContext ctx);
}
