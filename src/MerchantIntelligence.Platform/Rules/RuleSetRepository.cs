using System.Text.Json;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Storage;

namespace MerchantIntelligence.Platform.Rules;

public sealed record RuleSetVersion(int Version, string Author, string? Comment, DateTimeOffset CreatedAt, bool Active, int RuleCount);

/// <summary>Versioned rule-set storage. The embedded default is used until a custom set is published.</summary>
public sealed class RuleSetRepository
{
    private readonly PlatformDatabase _db;
    private readonly AuditTrail _audit;
    private readonly object _lock = new();
    private RuleSet? _active;

    public RuleSetRepository(PlatformDatabase db, AuditTrail audit)
    {
        _db = db;
        _audit = audit;
    }

    public RuleSet Active
    {
        get
        {
            lock (_lock)
            {
                if (_active is not null) return _active;
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT json FROM rule_sets WHERE active = 1 ORDER BY version DESC LIMIT 1";
                var json = cmd.ExecuteScalar() as string;
                _active = json is null ? RulesEngine.LoadDefault() : JsonSerializer.Deserialize<RuleSet>(json, RulesEngine.JsonOptions)!;
                return _active;
            }
        }
    }

    public RuleSetVersion Publish(RuleSet set, string author, string? comment)
    {
        RulesEngine.Validate(set);
        lock (_lock)
        {
            using var conn = _db.Open();
            using var tx = conn.BeginTransaction();
            using (var off = conn.CreateCommand())
            {
                off.CommandText = "UPDATE rule_sets SET active = 0";
                off.ExecuteNonQuery();
            }
            int version;
            var now = DateTimeOffset.UtcNow;
            using (var ins = conn.CreateCommand())
            {
                ins.CommandText = "INSERT INTO rule_sets (json, author, comment, created_at, active) VALUES ($j, $a, $c, $t, 1); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$j", JsonSerializer.Serialize(set, RulesEngine.JsonOptions));
                ins.Parameters.AddWithValue("$a", author);
                ins.Parameters.AddWithValue("$c", (object?)comment ?? DBNull.Value);
                ins.Parameters.AddWithValue("$t", now.ToString("O"));
                version = Convert.ToInt32(ins.ExecuteScalar());
            }
            tx.Commit();
            set.Version = version.ToString();
            _active = set;
            _audit.Record(null, author, "rules.published", new { version, comment, rules = set.Rules.Count });
            return new RuleSetVersion(version, author, comment, now, true, set.Rules.Count);
        }
    }

    public IReadOnlyList<RuleSetVersion> History()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, author, comment, created_at, active, json FROM rule_sets ORDER BY version DESC";
        var list = new List<RuleSetVersion>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var set = JsonSerializer.Deserialize<RuleSet>(r.GetString(5), RulesEngine.JsonOptions);
            list.Add(new RuleSetVersion(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                DateTimeOffset.Parse(r.GetString(3)), r.GetInt32(4) == 1, set?.Rules.Count ?? 0));
        }
        return list;
    }

    public RuleSet? GetVersion(int version)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM rule_sets WHERE version = $v";
        cmd.Parameters.AddWithValue("$v", version);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<RuleSet>(json, RulesEngine.JsonOptions) : null;
    }

    public RuleSetVersion Rollback(int version, string author)
    {
        var set = GetVersion(version) ?? throw new KeyNotFoundException($"Rule set version {version} not found.");
        return Publish(set, author, $"Rollback to version {version}");
    }
}
