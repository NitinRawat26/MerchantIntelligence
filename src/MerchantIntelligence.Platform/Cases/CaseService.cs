using System.Text.Json;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Storage;
using MerchantIntelligence.Platform.Webhooks;
using Microsoft.Data.Sqlite;

namespace MerchantIntelligence.Platform.Cases;

public enum CaseStatus
{
    Open,
    InReview,
    PendingDocuments,
    Approved,
    Declined,
    Withdrawn
}

public enum CasePriority
{
    Low,
    Normal,
    High,
    Urgent
}

public sealed record MerchantCase(
    string Id,
    string MerchantName,
    string? ExternalRef,
    CaseStatus Status,
    string? AssignedTo,
    CasePriority Priority,
    int? RiskScore,
    string? RiskTier,
    RuleOutcome? RulesOutcome,
    string? FinalDecision,
    JsonElement? Snapshot,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CaseNote(long Id, string CaseId, string Author, string Body, DateTimeOffset CreatedAt);

public sealed record CaseDecision(string Decision, string Actor, string Reason, bool IsOverride);

public sealed record CaseQueueStats(
    int Open, int InReview, int PendingDocuments, int Approved, int Declined, int Withdrawn,
    int Overrides, double AverageOpenAgeHours);

public sealed class CaseNotFoundException(string id) : Exception($"Case '{id}' not found.");
public sealed class InvalidCaseTransitionException(string message) : Exception(message);

/// <summary>
/// Analyst review queue. Every mutation writes an audit event and raises a webhook event. A decision that
/// contradicts the rules-engine outcome is recorded as an override with the analyst's stated reason.
/// </summary>
public sealed class CaseService
{
    private readonly PlatformDatabase _db;
    private readonly AuditTrail _audit;
    private readonly WebhookDispatcher _webhooks;

    public CaseService(PlatformDatabase db, AuditTrail audit, WebhookDispatcher webhooks)
    {
        _db = db;
        _audit = audit;
        _webhooks = webhooks;
    }

    public MerchantCase Create(string merchantName, string actor, string? externalRef = null, CasePriority? priority = null,
        int? riskScore = null, string? riskTier = null, RuleOutcome? rulesOutcome = null, object? snapshot = null)
    {
        var id = $"CASE-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var now = DateTimeOffset.UtcNow;
        var status = rulesOutcome switch
        {
            RuleOutcome.Approve => CaseStatus.Approved,
            RuleOutcome.Decline => CaseStatus.Declined,
            _ => CaseStatus.Open
        };
        var effectivePriority = priority ?? (riskScore is < 250 ? CasePriority.High : riskScore is < 450 ? CasePriority.Normal : CasePriority.Low);
        var snapshotJson = snapshot is null ? null : JsonSerializer.Serialize(snapshot, RulesEngine.JsonOptions);

        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO cases (id, merchant_name, external_ref, status, assigned_to, priority, risk_score, risk_tier, rules_outcome, final_decision, snapshot_json, created_at, updated_at)
                VALUES ($id, $name, $ref, $status, NULL, $prio, $score, $tier, $rules, $final, $snap, $now, $now)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", merchantName);
            cmd.Parameters.AddWithValue("$ref", (object?)externalRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$prio", effectivePriority.ToString());
            cmd.Parameters.AddWithValue("$score", (object?)riskScore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tier", (object?)riskTier ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rules", (object?)rulesOutcome?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$final", status is CaseStatus.Approved or CaseStatus.Declined ? status.ToString() : DBNull.Value);
            cmd.Parameters.AddWithValue("$snap", (object?)snapshotJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        _audit.Record(id, actor, "case.created", new { merchantName, riskScore, rulesOutcome, autoDecided = status != CaseStatus.Open });
        var created = Get(id);
        _webhooks.Publish("case.created", created);
        if (status != CaseStatus.Open) _webhooks.Publish("case.decided", created);
        return created;
    }

    public MerchantCase Get(string id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM cases WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new CaseNotFoundException(id);
        return Map(r);
    }

    public IReadOnlyList<MerchantCase> List(CaseStatus? status = null, string? assignedTo = null, int limit = 100)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM cases
            WHERE ($status IS NULL OR status = $status) AND ($who IS NULL OR assigned_to = $who)
            ORDER BY CASE priority WHEN 'Urgent' THEN 0 WHEN 'High' THEN 1 WHEN 'Normal' THEN 2 ELSE 3 END, created_at
            LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$status", (object?)status?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$who", (object?)assignedTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<MerchantCase>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public MerchantCase Assign(string id, string assignee, string actor)
    {
        var c = Get(id);
        EnsureOpen(c);
        Update(id, "assigned_to = $v, status = $s", ("$v", assignee), ("$s", CaseStatus.InReview.ToString()));
        _audit.Record(id, actor, "case.assigned", new { assignee, previous = c.AssignedTo });
        var updated = Get(id);
        _webhooks.Publish("case.assigned", updated);
        return updated;
    }

    public MerchantCase SetStatus(string id, CaseStatus status, string actor, string? reason = null)
    {
        var c = Get(id);
        if (status is CaseStatus.Approved or CaseStatus.Declined)
            throw new InvalidCaseTransitionException("Use Decide to approve or decline a case.");
        if (c.Status is CaseStatus.Approved or CaseStatus.Declined && status != CaseStatus.Withdrawn)
            throw new InvalidCaseTransitionException($"Case is already {c.Status}; reopen is not permitted (create a new case).");
        Update(id, "status = $s", ("$s", status.ToString()));
        _audit.Record(id, actor, "case.status_changed", new { from = c.Status, to = status, reason });
        var updated = Get(id);
        _webhooks.Publish("case.status_changed", updated);
        return updated;
    }

    public CaseNote AddNote(string id, string author, string body)
    {
        Get(id);
        long noteId;
        var now = DateTimeOffset.UtcNow;
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO case_notes (case_id, author, body, created_at) VALUES ($id, $a, $b, $t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$a", author);
            cmd.Parameters.AddWithValue("$b", body);
            cmd.Parameters.AddWithValue("$t", now.ToString("O"));
            noteId = (long)cmd.ExecuteScalar()!;
        }
        Update(id, "updated_at = $now");
        _audit.Record(id, author, "case.note_added", new { noteId });
        return new CaseNote(noteId, id, author, body, now);
    }

    public IReadOnlyList<CaseNote> Notes(string id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, case_id, author, body, created_at FROM case_notes WHERE case_id = $id ORDER BY id";
        cmd.Parameters.AddWithValue("$id", id);
        var list = new List<CaseNote>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new CaseNote(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4))));
        return list;
    }

    public MerchantCase Decide(string id, CaseDecision decision)
    {
        var c = Get(id);
        EnsureOpen(c);
        var status = decision.Decision.Equals("Approved", StringComparison.OrdinalIgnoreCase) ? CaseStatus.Approved
            : decision.Decision.Equals("Declined", StringComparison.OrdinalIgnoreCase) ? CaseStatus.Declined
            : throw new InvalidCaseTransitionException("Decision must be Approved or Declined.");

        var contradictsRules = c.RulesOutcome is RuleOutcome.Approve && status == CaseStatus.Declined
                            || c.RulesOutcome is RuleOutcome.Decline && status == CaseStatus.Approved;
        var isOverride = decision.IsOverride || contradictsRules;
        if (isOverride && string.IsNullOrWhiteSpace(decision.Reason))
            throw new InvalidCaseTransitionException("An override reason is required when the decision contradicts the rules outcome.");

        Update(id, "status = $s, final_decision = $s", ("$s", status.ToString()));
        _audit.Record(id, decision.Actor, isOverride ? "case.overridden" : "case.decided",
            new { decision = status, decision.Reason, rulesOutcome = c.RulesOutcome, isOverride });
        var updated = Get(id);
        _webhooks.Publish("case.decided", new { Case = updated, decision.Reason, IsOverride = isOverride });
        return updated;
    }

    public CaseQueueStats Stats()
    {
        using var conn = _db.Open();
        var counts = new Dictionary<string, int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT status, COUNT(*) FROM cases GROUP BY status";
            using var r = cmd.ExecuteReader();
            while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
        }
        int overrides;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'case.overridden'";
            overrides = Convert.ToInt32(cmd.ExecuteScalar());
        }
        double avgAge = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT created_at FROM cases WHERE status IN ('Open','InReview','PendingDocuments')";
            using var r = cmd.ExecuteReader();
            var ages = new List<double>();
            while (r.Read()) ages.Add((DateTimeOffset.UtcNow - DateTimeOffset.Parse(r.GetString(0))).TotalHours);
            if (ages.Count > 0) avgAge = Math.Round(ages.Average(), 2);
        }
        int Count(CaseStatus s) => counts.GetValueOrDefault(s.ToString());
        return new CaseQueueStats(Count(CaseStatus.Open), Count(CaseStatus.InReview), Count(CaseStatus.PendingDocuments),
            Count(CaseStatus.Approved), Count(CaseStatus.Declined), Count(CaseStatus.Withdrawn), overrides, avgAge);
    }

    private static void EnsureOpen(MerchantCase c)
    {
        if (c.Status is CaseStatus.Approved or CaseStatus.Declined or CaseStatus.Withdrawn)
            throw new InvalidCaseTransitionException($"Case is {c.Status} and can no longer be modified.");
    }

    private void Update(string id, string setClause, params (string Name, object Value)[] parameters)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE cases SET {setClause}{(setClause.Contains("updated_at") ? "" : ", updated_at = $now")} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private const string Columns = "id, merchant_name, external_ref, status, assigned_to, priority, risk_score, risk_tier, rules_outcome, final_decision, snapshot_json, created_at, updated_at";

    private static MerchantCase Map(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        Enum.Parse<CaseStatus>(r.GetString(3)),
        r.IsDBNull(4) ? null : r.GetString(4),
        Enum.Parse<CasePriority>(r.GetString(5)),
        r.IsDBNull(6) ? null : r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : Enum.Parse<RuleOutcome>(r.GetString(8)),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : JsonDocument.Parse(r.GetString(10)).RootElement,
        DateTimeOffset.Parse(r.GetString(11)),
        DateTimeOffset.Parse(r.GetString(12)));
}
