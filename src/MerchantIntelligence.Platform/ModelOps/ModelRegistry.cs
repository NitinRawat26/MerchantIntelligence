using System.Text.Json;
using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Storage;
using MerchantIntelligence.Platform.Webhooks;
using Microsoft.ML;

namespace MerchantIntelligence.Platform.ModelOps;

public enum ModelRole
{
    Champion,
    Challenger,
    Retired
}

public sealed record RegisteredModel(string Version, string Path, ModelRole Role, TrainingMetrics? Metrics, int? TrainingRows, DateTimeOffset RegisteredAt);

/// <summary>
/// Tracks credit-decision model versions on disk and which one is live. The champion serves
/// <see cref="IDecisionPredictor"/>; an optional challenger is shadow-scored on every prediction so its
/// accuracy can be compared on realised outcomes before promotion.
/// </summary>
public sealed class ModelRegistry : IDecisionPredictor
{
    private readonly PlatformDatabase _db;
    private readonly AuditTrail _audit;
    private readonly WebhookDispatcher _webhooks;
    private readonly PlatformOptions _options;
    private readonly object _lock = new();
    private (string Version, IDecisionPredictor Predictor) _champion;
    private (string Version, IDecisionPredictor Predictor)? _challenger;

    public ModelRegistry(PlatformDatabase db, AuditTrail audit, WebhookDispatcher webhooks, PlatformOptions options,
        IDecisionPredictor bootstrapChampion, string bootstrapPath)
    {
        _db = db;
        _audit = audit;
        _webhooks = webhooks;
        _options = options;

        var current = All().FirstOrDefault(m => m.Role == ModelRole.Champion);
        if (current is not null && File.Exists(current.Path) && current.Path != Path.GetFullPath(bootstrapPath))
        {
            _champion = (current.Version, DecisionPredictor.Load(current.Path));
        }
        else
        {
            var version = current?.Version ?? "v1-bootstrap";
            if (current is null) Insert(version, Path.GetFullPath(bootstrapPath), ModelRole.Champion, null, null);
            _champion = (version, bootstrapChampion);
        }
        var challenger = All().FirstOrDefault(m => m.Role == ModelRole.Challenger);
        if (challenger is not null && File.Exists(challenger.Path))
            _challenger = (challenger.Version, DecisionPredictor.Load(challenger.Path));
    }

    public string ChampionVersion => _champion.Version;
    public string? ChallengerVersion => _challenger?.Version;

    public DecisionResult Predict(MerchantApplication application) => _champion.Predictor.Predict(application);

    public DecisionResult? PredictChallenger(MerchantApplication application) => _challenger?.Predictor.Predict(application);

    public IReadOnlyList<RegisteredModel> All()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, path, role, metrics_json, training_rows, registered_at FROM model_registry ORDER BY registered_at DESC";
        var list = new List<RegisteredModel>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RegisteredModel(r.GetString(0), r.GetString(1), Enum.Parse<ModelRole>(r.GetString(2)),
                r.IsDBNull(3) ? null : JsonSerializer.Deserialize<TrainingMetrics>(r.GetString(3), RulesEngine.JsonOptions),
                r.IsDBNull(4) ? null : r.GetInt32(4), DateTimeOffset.Parse(r.GetString(5))));
        return list;
    }

    /// <summary>Registers a trained model file as the challenger (replacing any existing one).</summary>
    public RegisteredModel RegisterChallenger(string version, string path, TrainingMetrics? metrics, int? trainingRows, string actor)
    {
        var predictor = DecisionPredictor.Load(path);
        lock (_lock)
        {
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE model_registry SET role = 'Retired' WHERE role = 'Challenger'";
                cmd.ExecuteNonQuery();
            }
            Insert(version, Path.GetFullPath(path), ModelRole.Challenger, metrics, trainingRows);
            _challenger = (version, predictor);
        }
        _audit.Record(null, actor, "model.challenger_registered", new { version, metrics, trainingRows });
        return All().First(m => m.Version == version);
    }

    /// <summary>Makes the current challenger the champion; the old champion is retired.</summary>
    public RegisteredModel PromoteChallenger(string actor, string? justification)
    {
        lock (_lock)
        {
            if (_challenger is null) throw new InvalidOperationException("No challenger registered.");
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE model_registry SET role = 'Retired' WHERE role = 'Champion';
                    UPDATE model_registry SET role = 'Champion' WHERE version = $v;
                    """;
                cmd.Parameters.AddWithValue("$v", _challenger.Value.Version);
                cmd.ExecuteNonQuery();
            }
            var previous = _champion.Version;
            _champion = _challenger.Value;
            _challenger = null;
            _audit.Record(null, actor, "model.promoted", new { from = previous, to = _champion.Version, justification });
            var promoted = All().First(m => m.Version == _champion.Version);
            _webhooks.Publish("model.promoted", new { previous, promoted });
            return promoted;
        }
    }

    private void Insert(string version, string path, ModelRole role, TrainingMetrics? metrics, int? rows)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO model_registry (version, path, role, metrics_json, training_rows, registered_at) VALUES ($v, $p, $r, $m, $n, $t)";
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$r", role.ToString());
        cmd.Parameters.AddWithValue("$m", metrics is null ? DBNull.Value : JsonSerializer.Serialize(metrics, RulesEngine.JsonOptions));
        cmd.Parameters.AddWithValue("$n", (object?)rows ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public string ModelsDirectory => Path.GetFullPath(_options.ModelsDirectory);
}
