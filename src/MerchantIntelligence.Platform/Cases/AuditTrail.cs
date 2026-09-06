using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MerchantIntelligence.Platform.Storage;
using Microsoft.Data.Sqlite;

namespace MerchantIntelligence.Platform.Cases;

public sealed record AuditEvent(
    long Seq,
    string? CaseId,
    string Actor,
    string Action,
    JsonElement? Detail,
    DateTimeOffset OccurredAt,
    string PreviousHash,
    string Hash);

public sealed record AuditVerification(bool Valid, long EventsChecked, long? FirstBrokenSeq);

/// <summary>
/// Append-only, hash-chained audit log: each event's hash covers its content plus the previous hash, so
/// any edit or deletion in the table is detectable via <see cref="Verify"/>.
/// </summary>
public sealed class AuditTrail
{
    private readonly PlatformDatabase _db;
    private readonly object _lock = new();

    public AuditTrail(PlatformDatabase db) => _db = db;

    public AuditEvent Record(string? caseId, string actor, string action, object? detail = null)
    {
        lock (_lock)
        {
            using var conn = _db.Open();
            using var tx = conn.BeginTransaction();
            var prev = Scalar<string>(conn, "SELECT hash FROM audit_events ORDER BY seq DESC LIMIT 1") ?? "GENESIS";
            var at = DateTimeOffset.UtcNow;
            var detailJson = detail is null ? null : JsonSerializer.Serialize(detail, Rules.RulesEngine.JsonOptions);
            var hash = ComputeHash(caseId, actor, action, detailJson, at, prev);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_events (case_id, actor, action, detail_json, occurred_at, prev_hash, hash)
                VALUES ($case, $actor, $action, $detail, $at, $prev, $hash);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$case", (object?)caseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$actor", actor);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$detail", (object?)detailJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", at.ToString("O"));
            cmd.Parameters.AddWithValue("$prev", prev);
            cmd.Parameters.AddWithValue("$hash", hash);
            var seq = (long)cmd.ExecuteScalar()!;
            tx.Commit();

            return new AuditEvent(seq, caseId, actor, action,
                detailJson is null ? null : JsonDocument.Parse(detailJson).RootElement, at, prev, hash);
        }
    }

    public IReadOnlyList<AuditEvent> ForCase(string caseId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seq, case_id, actor, action, detail_json, occurred_at, prev_hash, hash FROM audit_events WHERE case_id = $id ORDER BY seq";
        cmd.Parameters.AddWithValue("$id", caseId);
        return Read(cmd);
    }

    public IReadOnlyList<AuditEvent> Recent(int limit = 100)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seq, case_id, actor, action, detail_json, occurred_at, prev_hash, hash FROM audit_events ORDER BY seq DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        return Read(cmd);
    }

    public AuditVerification Verify()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seq, case_id, actor, action, detail_json, occurred_at, prev_hash, hash FROM audit_events ORDER BY seq";
        var prev = "GENESIS";
        long checkedCount = 0;
        foreach (var e in Read(cmd))
        {
            var expected = ComputeHash(e.CaseId, e.Actor, e.Action,
                e.Detail is null ? null : e.Detail.Value.GetRawText(), e.OccurredAt, prev);
            if (e.PreviousHash != prev || e.Hash != expected)
                return new AuditVerification(false, checkedCount, e.Seq);
            prev = e.Hash;
            checkedCount++;
        }
        return new AuditVerification(true, checkedCount, null);
    }

    private static string ComputeHash(string? caseId, string actor, string action, string? detailJson, DateTimeOffset at, string prev)
    {
        var payload = string.Join('\u001f', caseId ?? "", actor, action, detailJson ?? "", at.ToString("O"), prev);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static List<AuditEvent> Read(SqliteCommand cmd)
    {
        var list = new List<AuditEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var detail = r.IsDBNull(4) ? null : r.GetString(4);
            list.Add(new AuditEvent(
                r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                detail is null ? null : JsonDocument.Parse(detail).RootElement,
                DateTimeOffset.Parse(r.GetString(5)),
                r.GetString(6),
                r.GetString(7)));
        }
        return list;
    }

    private static T? Scalar<T>(SqliteConnection conn, string sql) where T : class
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as T;
    }
}
