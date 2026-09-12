using System.Reflection;
using System.Text.Json;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Storage;

namespace MerchantIntelligence.Platform.Workflows;

/// <summary>Versioned workflow storage. The embedded default pipeline is active until an operator publishes one.</summary>
public sealed class WorkflowRepository
{
    private readonly PlatformDatabase _db;
    private readonly AuditTrail _audit;
    private readonly WorkflowPlanner _planner;
    private readonly object _lock = new();
    private WorkflowDefinition? _active;

    public WorkflowRepository(PlatformDatabase db, AuditTrail audit, WorkflowPlanner planner)
    {
        _db = db;
        _audit = audit;
        _planner = planner;
    }

    public static WorkflowDefinition LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MerchantIntelligence.Platform.Resources.default-workflow.json")
            ?? throw new InvalidOperationException("default-workflow.json missing");
        return JsonSerializer.Deserialize<WorkflowDefinition>(stream, RulesEngine.JsonOptions) ?? new WorkflowDefinition();
    }

    public WorkflowDefinition Active
    {
        get
        {
            lock (_lock)
            {
                if (_active is not null) return _active;
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT json FROM assessment_workflows WHERE active = 1 ORDER BY version DESC LIMIT 1";
                _active = cmd.ExecuteScalar() is string json ? Parse(json) : LoadDefault();
                return _active;
            }
        }
    }

    public WorkflowVersion Publish(WorkflowDefinition def, string author, string? comment)
    {
        _planner.Validate(def);
        lock (_lock)
        {
            using var conn = _db.Open();
            using var tx = conn.BeginTransaction();
            using (var off = conn.CreateCommand())
            {
                off.CommandText = "UPDATE assessment_workflows SET active = 0";
                off.ExecuteNonQuery();
            }
            int version;
            var now = DateTimeOffset.UtcNow;
            using (var ins = conn.CreateCommand())
            {
                ins.CommandText = "INSERT INTO assessment_workflows (json, author, comment, created_at, active) VALUES ($j, $a, $c, $t, 1); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$j", JsonSerializer.Serialize(def, RulesEngine.JsonOptions));
                ins.Parameters.AddWithValue("$a", author);
                ins.Parameters.AddWithValue("$c", (object?)comment ?? DBNull.Value);
                ins.Parameters.AddWithValue("$t", now.ToString("O"));
                version = Convert.ToInt32(ins.ExecuteScalar());
            }
            using (var upd = conn.CreateCommand())
            {
                def.Version = version.ToString();
                upd.CommandText = "UPDATE assessment_workflows SET json = $j WHERE version = $v";
                upd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(def, RulesEngine.JsonOptions));
                upd.Parameters.AddWithValue("$v", version);
                upd.ExecuteNonQuery();
            }
            tx.Commit();
            _active = def;
            var enabled = def.Steps.Count(s => s.Enabled);
            _audit.Record(null, author, "workflow.published", new { version, comment, name = def.Name, enabled, steps = def.Steps.Count, order = def.Steps.Where(s => s.Enabled).Select(s => s.Id) });
            return new WorkflowVersion(version, def.Name, author, comment, now, true, enabled, def.Steps.Count);
        }
    }

    public IReadOnlyList<WorkflowVersion> History()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, author, comment, created_at, active, json FROM assessment_workflows ORDER BY version DESC";
        var list = new List<WorkflowVersion>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var def = Parse(r.GetString(5));
            list.Add(new WorkflowVersion(r.GetInt32(0), def.Name, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                DateTimeOffset.Parse(r.GetString(3)), r.GetInt32(4) == 1, def.Steps.Count(s => s.Enabled), def.Steps.Count));
        }
        return list;
    }

    public WorkflowDefinition? GetVersion(int version)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM assessment_workflows WHERE version = $v";
        cmd.Parameters.AddWithValue("$v", version);
        return cmd.ExecuteScalar() is string json ? Parse(json) : null;
    }

    public WorkflowVersion Rollback(int version, string author)
    {
        var def = GetVersion(version) ?? throw new KeyNotFoundException($"Workflow version {version} not found.");
        return Publish(def, author, $"Rollback to version {version}");
    }

    private static WorkflowDefinition Parse(string json) =>
        JsonSerializer.Deserialize<WorkflowDefinition>(json, RulesEngine.JsonOptions) ?? throw new InvalidOperationException("Stored workflow is not valid JSON.");
}
