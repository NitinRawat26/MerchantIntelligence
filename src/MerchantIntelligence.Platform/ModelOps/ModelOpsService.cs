using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Platform.Cases;
using MerchantIntelligence.Platform.Storage;
using MerchantIntelligence.Platform.Webhooks;
using Microsoft.ML;

namespace MerchantIntelligence.Platform.ModelOps;

public sealed record LoggedDecision(long Id, string? CaseId, string ModelVersion, MerchantApplication Application,
    Decision Predicted, double Confidence, Decision? ChallengerPredicted, Decision? Actual, DateTimeOffset ScoredAt);

public sealed record FeatureDrift(string Feature, double Psi, string Status, IReadOnlyList<double> ReferenceShare, IReadOnlyList<double> RecentShare);

public sealed record DriftReport(
    int ReferenceRows,
    int RecentRows,
    DateTimeOffset? Since,
    IReadOnlyList<FeatureDrift> Features,
    double PredictionDriftPsi,
    string OverallStatus,
    IReadOnlyList<string> Alerts);

public sealed record ModelPerformance(string Version, int Scored, int WithOutcome, double? Accuracy, double? ApprovalPrecision, double? DeclineRecall, IReadOnlyDictionary<string, int> Confusion);

public sealed record ChampionChallengerReport(ModelPerformance Champion, ModelPerformance? Challenger, int Disagreements, string Recommendation);

public sealed record RetrainResult(string Version, string Path, TrainingMetrics Metrics, int LabelledRows, int SyntheticRows, bool RegisteredAsChallenger);

/// <summary>
/// Decision logging, population-stability drift monitoring, champion/challenger comparison and retraining.
/// Drift uses PSI against a reference sample (the synthetic training distribution by default):
/// &lt; 0.10 stable, 0.10–0.25 moderate, &gt; 0.25 significant.
/// </summary>
public sealed class ModelOpsService
{
    private readonly PlatformDatabase _db;
    private readonly ModelRegistry _registry;
    private readonly AuditTrail _audit;
    private readonly WebhookDispatcher _webhooks;

    private static readonly float[] VolumeBins = [0, 50_000, 150_000, 500_000, 1_500_000, 5_000_000, 20_000_000, float.MaxValue];
    private static readonly float[] TicketBins = [0, 15, 40, 100, 300, 1_000, 5_000, float.MaxValue];
    private static readonly float[] SpreadBins = [0, 2, 5, 10, 20, 40, float.MaxValue];

    public ModelOpsService(PlatformDatabase db, ModelRegistry registry, AuditTrail audit, WebhookDispatcher webhooks)
    {
        _db = db;
        _registry = registry;
        _audit = audit;
        _webhooks = webhooks;
    }

    /// <summary>Scores with the champion, shadow-scores with the challenger, and logs both.</summary>
    public (DecisionResult Result, long LogId) PredictAndLog(MerchantApplication app, string? caseId = null)
    {
        var result = _registry.Predict(app);
        var shadow = _registry.PredictChallenger(app);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO decision_log (case_id, model_version, mcc, annual_volume, average_ticket, highest_ticket, match_found, existing_relationship,
                predicted, confidence, challenger_predicted, challenger_confidence, scored_at)
            VALUES ($case, $ver, $mcc, $vol, $avg, $hi, $match, $rel, $pred, $conf, $cpred, $cconf, $at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$case", (object?)caseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ver", _registry.ChampionVersion);
        cmd.Parameters.AddWithValue("$mcc", app.MerchantCategoryCode);
        cmd.Parameters.AddWithValue("$vol", app.AnnualVolume);
        cmd.Parameters.AddWithValue("$avg", app.AverageTicket);
        cmd.Parameters.AddWithValue("$hi", app.HighestTicket);
        cmd.Parameters.AddWithValue("$match", app.MatchFound ? 1 : 0);
        cmd.Parameters.AddWithValue("$rel", app.ExistingRelationship ? 1 : 0);
        cmd.Parameters.AddWithValue("$pred", result.Decision.ToString());
        cmd.Parameters.AddWithValue("$conf", result.Confidence);
        cmd.Parameters.AddWithValue("$cpred", (object?)shadow?.Decision.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cconf", (object?)shadow?.Confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        var id = (long)cmd.ExecuteScalar()!;
        return (result, id);
    }

    /// <summary>Records the realised outcome (analyst decision or post-boarding result) for a logged prediction.</summary>
    public bool RecordOutcome(long logId, Decision actual, string actor)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE decision_log SET actual = $a, outcome_at = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", logId);
        cmd.Parameters.AddWithValue("$a", actual.ToString());
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        var ok = cmd.ExecuteNonQuery() > 0;
        if (ok) _audit.Record(null, actor, "model.outcome_recorded", new { logId, actual });
        return ok;
    }

    public IReadOnlyList<LoggedDecision> Recent(int limit = 100, bool onlyLabelled = false)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, case_id, model_version, mcc, annual_volume, average_ticket, highest_ticket, match_found, existing_relationship, predicted, confidence, challenger_predicted, actual, scored_at FROM decision_log {(onlyLabelled ? "WHERE actual IS NOT NULL" : "")} ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<LoggedDecision>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new LoggedDecision(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                new MerchantApplication
                {
                    MerchantCategoryCode = r.GetFloat(3), AnnualVolume = r.GetFloat(4), AverageTicket = r.GetFloat(5), HighestTicket = r.GetFloat(6),
                    MatchFound = r.GetInt32(7) == 1, ExistingRelationship = r.GetInt32(8) == 1
                },
                Enum.Parse<Decision>(r.GetString(9)), r.GetDouble(10),
                r.IsDBNull(11) ? null : Enum.Parse<Decision>(r.GetString(11)),
                r.IsDBNull(12) ? null : Enum.Parse<Decision>(r.GetString(12)),
                DateTimeOffset.Parse(r.GetString(13))));
        return list;
    }

    public DriftReport Drift(DateTimeOffset? since = null, int referenceSize = 5000)
    {
        var recent = Recent(int.MaxValue).Where(d => since is null || d.ScoredAt >= since).Select(d => d.Application).ToList();
        var reference = SyntheticDataGenerator.Generate(referenceSize).Select(ToApp).ToList();
        var referencePredictions = SyntheticDataGenerator.Generate(referenceSize).Select(r => Enum.Parse<Decision>(r.Decision)).ToList();
        var recentPredictions = Recent(int.MaxValue).Where(d => since is null || d.ScoredAt >= since).Select(d => d.Predicted).ToList();

        var features = new List<FeatureDrift>();
        var alerts = new List<string>();
        if (recent.Count < 30)
        {
            alerts.Add($"Only {recent.Count} recent decisions logged; drift statistics need at least 30.");
            return new DriftReport(reference.Count, recent.Count, since, features, 0, "InsufficientData", alerts);
        }

        features.Add(Psi("AnnualVolume", reference.Select(a => a.AnnualVolume), recent.Select(a => a.AnnualVolume), VolumeBins));
        features.Add(Psi("AverageTicket", reference.Select(a => a.AverageTicket), recent.Select(a => a.AverageTicket), TicketBins));
        features.Add(Psi("TicketSpread", reference.Select(a => a.HighestTicket / Math.Max(a.AverageTicket, 1)), recent.Select(a => a.HighestTicket / Math.Max(a.AverageTicket, 1)), SpreadBins));
        features.Add(PsiCategorical("MccRiskTier", reference.Select(MccTier), recent.Select(MccTier), ["Low", "Medium", "High"]));
        features.Add(PsiCategorical("MatchFound", reference.Select(a => a.MatchFound.ToString()), recent.Select(a => a.MatchFound.ToString()), ["False", "True"]));
        features.Add(PsiCategorical("ExistingRelationship", reference.Select(a => a.ExistingRelationship.ToString()), recent.Select(a => a.ExistingRelationship.ToString()), ["False", "True"]));

        var prediction = PsiCategorical("PredictedDecision", referencePredictions.Select(p => p.ToString()), recentPredictions.Select(p => p.ToString()),
            Enum.GetNames<Decision>());

        foreach (var f in features.Where(f => f.Status == "Significant")) alerts.Add($"{f.Feature} distribution shifted (PSI {f.Psi:F3}).");
        if (prediction.Status == "Significant") alerts.Add($"Prediction mix shifted (PSI {prediction.Psi:F3}); check for population change or model degradation.");

        var overall = features.Append(prediction).Max(f => f.Psi) switch { > 0.25 => "Significant", > 0.10 => "Moderate", _ => "Stable" };
        if (overall == "Significant") _webhooks.Publish("model.drift_alert", new { overall, alerts });
        return new DriftReport(reference.Count, recent.Count, since, features, prediction.Psi, overall, alerts);
    }

    public ChampionChallengerReport Compare()
    {
        var labelled = Recent(int.MaxValue, onlyLabelled: true);
        var all = Recent(int.MaxValue);
        var champion = Performance(_registry.ChampionVersion, all.Count, labelled, d => d.Predicted);
        ModelPerformance? challenger = null;
        var disagreements = 0;
        if (_registry.ChallengerVersion is { } cv)
        {
            var shadowed = all.Where(d => d.ChallengerPredicted is not null).ToList();
            disagreements = shadowed.Count(d => d.ChallengerPredicted != d.Predicted);
            challenger = Performance(cv, shadowed.Count, labelled.Where(d => d.ChallengerPredicted is not null).ToList(), d => d.ChallengerPredicted!.Value);
        }
        var recommendation = challenger is null ? "No challenger registered; train one via /retrain."
            : challenger.WithOutcome < 50 ? $"Only {challenger.WithOutcome} labelled outcomes for the challenger; keep shadowing (need ≥ 50)."
            : challenger.Accuracy > champion.Accuracy + 0.02 ? "Challenger outperforms champion by > 2pp; consider promotion."
            : challenger.Accuracy < champion.Accuracy - 0.02 ? "Challenger underperforms; retire it."
            : "No material difference yet; keep shadowing.";
        return new ChampionChallengerReport(champion, challenger, disagreements, recommendation);
    }

    /// <summary>
    /// Retrains on logged decisions with realised outcomes, topped up with synthetic rows so the model keeps
    /// broad coverage when real history is thin. The new model is saved and registered as challenger.
    /// </summary>
    public RetrainResult Retrain(string actor, int syntheticRows = 20_000, bool registerAsChallenger = true)
    {
        var labelled = Recent(int.MaxValue, onlyLabelled: true)
            .Select(d => new MerchantApplicationRecord
            {
                MerchantCategoryCode = d.Application.MerchantCategoryCode, AnnualVolume = d.Application.AnnualVolume,
                AverageTicket = d.Application.AverageTicket, HighestTicket = d.Application.HighestTicket,
                MatchFound = d.Application.MatchFound, ExistingRelationship = d.Application.ExistingRelationship,
                Decision = d.Actual!.Value.ToString()
            }).ToList();
        var synthetic = SyntheticDataGenerator.Generate(syntheticRows, seed: Environment.TickCount).ToList();
        var rows = labelled.Concat(synthetic).ToList();

        var ml = new MLContext(seed: 42);
        var (model, metrics) = ModelTrainer.Train(ml, rows);
        var version = $"v{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var path = Path.Combine(_registry.ModelsDirectory, $"credit-decision-{version}.zip");
        ModelTrainer.Save(ml, model, rows.Take(10), path);

        _audit.Record(null, actor, "model.retrained", new { version, labelledRows = labelled.Count, syntheticRows, metrics });
        if (registerAsChallenger) _registry.RegisterChallenger(version, path, metrics, rows.Count, actor);
        return new RetrainResult(version, path, metrics, labelled.Count, syntheticRows, registerAsChallenger);
    }

    private static ModelPerformance Performance(string version, int scored, IReadOnlyList<LoggedDecision> labelled, Func<LoggedDecision, Decision> pick)
    {
        var confusion = new Dictionary<string, int>();
        foreach (var d in labelled)
        {
            var key = $"{pick(d)}->{d.Actual}";
            confusion[key] = confusion.GetValueOrDefault(key) + 1;
        }
        if (labelled.Count == 0) return new ModelPerformance(version, scored, 0, null, null, null, confusion);
        var correct = labelled.Count(d => pick(d) == d.Actual);
        var predictedApprove = labelled.Where(d => pick(d) == Decision.Approved).ToList();
        var actualDecline = labelled.Where(d => d.Actual == Decision.Declined).ToList();
        return new ModelPerformance(version, scored, labelled.Count,
            Math.Round((double)correct / labelled.Count, 4),
            predictedApprove.Count == 0 ? null : Math.Round((double)predictedApprove.Count(d => d.Actual == Decision.Approved) / predictedApprove.Count, 4),
            actualDecline.Count == 0 ? null : Math.Round((double)actualDecline.Count(d => pick(d) == Decision.Declined) / actualDecline.Count, 4),
            confusion);
    }

    private static FeatureDrift Psi(string name, IEnumerable<float> reference, IEnumerable<float> recent, float[] bins)
    {
        var refShare = Histogram(reference, bins);
        var recShare = Histogram(recent, bins);
        var psi = PsiValue(refShare, recShare);
        return new FeatureDrift(name, Math.Round(psi, 4), Status(psi), refShare, recShare);
    }

    private static FeatureDrift PsiCategorical(string name, IEnumerable<string> reference, IEnumerable<string> recent, string[] categories)
    {
        var refList = reference.ToList();
        var recList = recent.ToList();
        var refShare = categories.Select(c => refList.Count == 0 ? 0 : Math.Round((double)refList.Count(x => x == c) / refList.Count, 4)).ToList();
        var recShare = categories.Select(c => recList.Count == 0 ? 0 : Math.Round((double)recList.Count(x => x == c) / recList.Count, 4)).ToList();
        var psi = PsiValue(refShare, recShare);
        return new FeatureDrift(name, Math.Round(psi, 4), Status(psi), refShare, recShare);
    }

    private static List<double> Histogram(IEnumerable<float> values, float[] bins)
    {
        var list = values.ToList();
        var counts = new int[bins.Length - 1];
        foreach (var v in list)
        {
            for (var i = 0; i < counts.Length; i++)
                if (v >= bins[i] && v < bins[i + 1]) { counts[i]++; break; }
        }
        return counts.Select(c => list.Count == 0 ? 0 : Math.Round((double)c / list.Count, 4)).ToList();
    }

    private static double PsiValue(IReadOnlyList<double> reference, IReadOnlyList<double> recent)
    {
        const double eps = 1e-4;
        double psi = 0;
        for (var i = 0; i < reference.Count; i++)
        {
            var r = Math.Max(reference[i], eps);
            var c = Math.Max(recent[i], eps);
            psi += (c - r) * Math.Log(c / r);
        }
        return psi;
    }

    private static string Status(double psi) => psi > 0.25 ? "Significant" : psi > 0.10 ? "Moderate" : "Stable";

    private static string MccTier(MerchantApplication a)
    {
        var mcc = (int)a.MerchantCategoryCode;
        return MccValidation.Taxonomy.MccCatalog.Default.Find(mcc)?.RiskTier.ToString() ?? "Medium";
    }

    private static MerchantApplication ToApp(MerchantApplicationRecord r) => new()
    {
        MerchantCategoryCode = r.MerchantCategoryCode, AnnualVolume = r.AnnualVolume, AverageTicket = r.AverageTicket,
        HighestTicket = r.HighestTicket, MatchFound = r.MatchFound, ExistingRelationship = r.ExistingRelationship
    };
}
