using System.Text.Json;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Storage;
using MerchantIntelligence.Platform.Workflows;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Platform.Assessment;

/// <summary>
/// Runs the active assessment workflow against a single intake, tolerating individual step failures
/// (a failed check becomes a coverage gap rather than aborting the assessment), then folds the results
/// into the unified score, the policy rules and a human-readable explanation. Results are persisted so the
/// report can be re-opened and exported later.
/// </summary>
public sealed class AssessmentService
{
    private readonly WorkflowRunner _runner;
    private readonly WorkflowRepository _workflows;
    private readonly WorkflowPlanner _planner;
    private readonly AuditTrail _audit;
    private readonly PlatformDatabase _db;
    private readonly ILogger<AssessmentService> _logger;

    public AssessmentService(WorkflowRunner runner, WorkflowRepository workflows, WorkflowPlanner planner, AuditTrail audit, PlatformDatabase db, ILogger<AssessmentService> logger)
    {
        _runner = runner;
        _workflows = workflows;
        _planner = planner;
        _audit = audit;
        _db = db;
        _logger = logger;
        EnsureSchema();
    }

    /// <summary>The steps of the active workflow in execution order, disabled ones included so the UI can show them greyed out.</summary>
    public IReadOnlyList<AssessmentStepDescriptor> PlannedSteps(WorkflowDefinition? workflow = null)
    {
        var def = workflow ?? _workflows.Active;
        return def.Steps.Select(s =>
        {
            var d = _planner.Describe(s.Id);
            return new AssessmentStepDescriptor(d.Id, d.Name, s.Enabled);
        }).ToList();
    }

    public async Task<AssessmentResult> RunAsync(AssessmentIntake intake, UploadedDocument? bankStatement, UploadedDocument? financialStatement,
        Func<AssessmentStep, Task>? progress, CancellationToken ct = default, WorkflowDefinition? workflow = null)
    {
        var id = $"ASMT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var def = workflow ?? _workflows.Active;
        var ctx = new AssessmentContext(id, intake, bankStatement, financialStatement, def, progress ?? (_ => Task.CompletedTask), _logger, ct);
        _audit.Record(null, intake.Actor, "assessment.started", new { assessmentId = id, workflow = def.Name, workflowVersion = def.Version });

        await _runner.RunAsync(def, ctx);

        var steps = ctx.Steps;
        var decision = AssessmentComposer.BuildDecision(ctx.Score, ctx.Rules, steps, ctx.ForcedRefer);
        var explainability = AssessmentComposer.BuildExplainability(intake, decision, ctx.Verification, ctx.Screening, ctx.Website, ctx.Prohibited, ctx.Mcc, ctx.Match,
            ctx.Bank, ctx.Financials, ctx.Plausibility, ctx.Credit, ctx.CreditExplanation, ctx.Terms, ctx.Score, ctx.Rules, ctx.Signals);

        var result = new AssessmentResult(id, ctx.StartedAt, DateTimeOffset.UtcNow, AssessmentComposer.Summarise(intake, bankStatement, financialStatement), steps, decision, explainability,
            ctx.Verification, ctx.Screening, ctx.Website, ctx.Prohibited, ctx.Mcc, ctx.Match, ctx.Bank, ctx.Financials, ctx.Plausibility, ctx.Credit, ctx.CreditExplanation,
            ctx.Terms, ctx.Score, ctx.Rules, ctx.Case, ctx.DecisionLogId, new AssessmentWorkflowInfo(def.Name, def.Version, def.Steps.Where(s => s.Enabled).Select(s => s.Id).ToList()));
        Persist(result);
        _audit.Record(ctx.Case?.Id, intake.Actor, "assessment.completed", new { assessmentId = id, decision.Outcome, decision.Score, decision.CoveragePercent, workflow = def.Name, workflowVersion = def.Version });
        return result;
    }


    // ---- persistence ----

    public AssessmentResult? Get(string id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM assessments WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<AssessmentResult>(json, RulesEngine.JsonOptions);
    }

    public IReadOnlyList<AssessmentListItem> List(int limit = 50)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, merchant_name, outcome, score, tier, coverage, case_id, completed_at FROM assessments ORDER BY completed_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<AssessmentListItem>();
        while (r.Read())
            list.Add(new AssessmentListItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetDouble(5),
                r.IsDBNull(6) ? null : r.GetString(6), DateTimeOffset.Parse(r.GetString(7))));
        return list;
    }

    private void Persist(AssessmentResult result)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO assessments (id, merchant_name, outcome, score, tier, coverage, case_id, completed_at, json)
            VALUES ($id, $name, $outcome, $score, $tier, $coverage, $case, $completed, $json)
            """;
        cmd.Parameters.AddWithValue("$id", result.Id);
        cmd.Parameters.AddWithValue("$name", result.Intake.Business.LegalName);
        cmd.Parameters.AddWithValue("$outcome", result.Decision.Outcome);
        cmd.Parameters.AddWithValue("$score", result.Decision.Score);
        cmd.Parameters.AddWithValue("$tier", result.Decision.Tier);
        cmd.Parameters.AddWithValue("$coverage", result.Decision.CoveragePercent);
        cmd.Parameters.AddWithValue("$case", (object?)result.Case?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completed", result.CompletedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(result, RulesEngine.JsonOptions));
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS assessments (
                id TEXT PRIMARY KEY,
                merchant_name TEXT NOT NULL,
                outcome TEXT NOT NULL,
                score INTEGER NOT NULL,
                tier TEXT NOT NULL,
                coverage REAL NOT NULL,
                case_id TEXT,
                completed_at TEXT NOT NULL,
                json TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
