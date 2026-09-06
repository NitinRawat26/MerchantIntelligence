using MerchantIntelligence.CreditDecision;

namespace MerchantIntelligence.Underwriting.Explainability;

public sealed record FeatureContribution(
    string Feature,
    string Value,
    string BaselineValue,
    // Shapley value: change in probability of the explained class attributable to this feature.
    double Contribution,
    string Direction);

public sealed record ReasonCode(string Code, string Description, double Weight);

public sealed record DecisionExplanation(
    Decision Decision,
    double Confidence,
    Decision ExplainedClass,
    double BaselineProbability,
    double PredictedProbability,
    IReadOnlyList<FeatureContribution> Contributions,
    IReadOnlyList<ReasonCode> ReasonCodes,
    string Narrative);

/// <summary>
/// Model-agnostic, per-application explanation of a credit decision. Computes exact Shapley values
/// over the six input features by evaluating the predictor on every coalition (2^6 = 64 calls) where
/// features outside the coalition are replaced by a baseline "typical" application. The result is the
/// contribution of each feature to the probability of the explained class relative to that baseline,
/// suitable for adverse-action reason codes.
/// </summary>
public sealed class DecisionExplainer
{
    private static readonly string[] Features =
    {
        nameof(MerchantApplication.MerchantCategoryCode),
        nameof(MerchantApplication.AnnualVolume),
        nameof(MerchantApplication.AverageTicket),
        nameof(MerchantApplication.HighestTicket),
        nameof(MerchantApplication.MatchFound),
        nameof(MerchantApplication.ExistingRelationship)
    };

    private static readonly long[] Factorials = Enumerable.Range(0, 8).Select(n => Enumerable.Range(1, n).Aggregate(1L, (a, b) => a * b)).ToArray();

    private readonly IDecisionPredictor _predictor;
    private readonly MerchantApplication _baseline;

    public DecisionExplainer(IDecisionPredictor predictor, MerchantApplication? baseline = null)
    {
        _predictor = predictor;
        _baseline = baseline ?? DefaultBaseline;
    }

    /// <summary>A typical low-risk small merchant; feature contributions are measured relative to it.</summary>
    public static MerchantApplication DefaultBaseline => new()
    {
        MerchantCategoryCode = 5812,
        AnnualVolume = 500_000,
        AverageTicket = 60,
        HighestTicket = 400,
        MatchFound = false,
        ExistingRelationship = false
    };

    public DecisionExplanation Explain(MerchantApplication application, Decision? explainClass = null)
    {
        var actual = _predictor.Predict(application);
        var target = explainClass ?? actual.Decision;
        var n = Features.Length;

        // Cache every coalition's target-class probability.
        var coalitionValue = new double[1 << n];
        for (var mask = 0; mask < coalitionValue.Length; mask++)
            coalitionValue[mask] = Probability(Compose(application, mask), target);

        var shapley = new double[n];
        for (var i = 0; i < n; i++)
        {
            for (var mask = 0; mask < coalitionValue.Length; mask++)
            {
                if ((mask & (1 << i)) != 0) continue;
                var size = System.Numerics.BitOperations.PopCount((uint)mask);
                var weight = (double)Factorials[size] * Factorials[n - size - 1] / Factorials[n];
                shapley[i] += weight * (coalitionValue[mask | (1 << i)] - coalitionValue[mask]);
            }
        }

        var contributions = Features.Select((f, i) => new FeatureContribution(
                f,
                Format(application, i),
                Format(_baseline, i),
                Math.Round(shapley[i], 4),
                shapley[i] > 0.005 ? "Increases" : shapley[i] < -0.005 ? "Decreases" : "Neutral"))
            .OrderByDescending(c => Math.Abs(c.Contribution))
            .ToList();

        var reasonCodes = BuildReasonCodes(application, contributions, target);
        var narrative = BuildNarrative(actual, target, coalitionValue[0], coalitionValue[^1], contributions);

        return new DecisionExplanation(actual.Decision, actual.Confidence, target,
            Math.Round(coalitionValue[0], 4), Math.Round(coalitionValue[^1], 4), contributions, reasonCodes, narrative);
    }

    private double Probability(MerchantApplication app, Decision target) =>
        _predictor.Predict(app).Probabilities.GetValueOrDefault(target);

    private MerchantApplication Compose(MerchantApplication x, int mask) => new()
    {
        MerchantCategoryCode = (mask & 1) != 0 ? x.MerchantCategoryCode : _baseline.MerchantCategoryCode,
        AnnualVolume = (mask & 2) != 0 ? x.AnnualVolume : _baseline.AnnualVolume,
        AverageTicket = (mask & 4) != 0 ? x.AverageTicket : _baseline.AverageTicket,
        HighestTicket = (mask & 8) != 0 ? x.HighestTicket : _baseline.HighestTicket,
        MatchFound = (mask & 16) != 0 ? x.MatchFound : _baseline.MatchFound,
        ExistingRelationship = (mask & 32) != 0 ? x.ExistingRelationship : _baseline.ExistingRelationship
    };

    private static string Format(MerchantApplication a, int i) => i switch
    {
        0 => ((int)a.MerchantCategoryCode).ToString(),
        1 => a.AnnualVolume.ToString("N0"),
        2 => a.AverageTicket.ToString("N2"),
        3 => a.HighestTicket.ToString("N2"),
        4 => a.MatchFound.ToString(),
        _ => a.ExistingRelationship.ToString()
    };

    /// <summary>
    /// Reason codes are expressed in the approval direction regardless of the explained class:
    /// adverse codes for features that pushed away from approval, supportive codes for those that helped.
    /// Strongest adverse drivers come first (adverse-action notice convention).
    /// </summary>
    private static List<ReasonCode> BuildReasonCodes(MerchantApplication app, IReadOnlyList<FeatureContribution> contributions, Decision target)
    {
        var sign = target == Decision.Approved ? 1 : -1;
        var codes = new List<ReasonCode>();
        foreach (var c in contributions.Where(c => Math.Abs(c.Contribution) > 0.02).OrderBy(c => sign * c.Contribution).Take(4))
        {
            var adverse = sign * c.Contribution < 0;
            var (code, text) = c.Feature switch
            {
                nameof(MerchantApplication.MerchantCategoryCode) => adverse
                    ? ("HIGH_RISK_MCC", $"Merchant category {(int)app.MerchantCategoryCode} carries elevated chargeback/fraud exposure.")
                    : ("LOW_RISK_MCC", $"Merchant category {(int)app.MerchantCategoryCode} is a low-risk vertical."),
                nameof(MerchantApplication.AnnualVolume) => adverse
                    ? (app.AnnualVolume < 50_000 ? "VOLUME_TOO_LOW" : "VOLUME_EXPOSURE", $"Declared annual volume {app.AnnualVolume:N0} is {(app.AnnualVolume < 50_000 ? "too low to be economical" : "a large credit exposure")}.")
                    : ("VOLUME_APPROPRIATE", "Declared annual volume is within normal underwriting limits."),
                nameof(MerchantApplication.AverageTicket) => adverse
                    ? ("HIGH_AVERAGE_TICKET", $"Average ticket {app.AverageTicket:N2} increases per-transaction chargeback exposure.")
                    : ("MODERATE_AVERAGE_TICKET", "Average ticket size is moderate."),
                nameof(MerchantApplication.HighestTicket) => adverse
                    ? ("TICKET_SPREAD", $"Highest ticket {app.HighestTicket:N2} is {app.HighestTicket / Math.Max(app.AverageTicket, 1):N0}x the average, indicating irregular large sales.")
                    : ("CONSISTENT_TICKETS", "Ticket sizes are consistent."),
                nameof(MerchantApplication.MatchFound) => adverse
                    ? ("MATCH_LISTED", "Principal or business appears on the terminated-merchant (MATCH) file.")
                    : ("NO_MATCH_RECORD", "No terminated-merchant record found."),
                _ => adverse
                    ? ("NO_EXISTING_RELATIONSHIP", "No prior relationship or performance history with the acquirer.")
                    : ("EXISTING_RELATIONSHIP", "Existing relationship in good standing.")
            };
            codes.Add(new ReasonCode(code, text, Math.Round(c.Contribution, 4)));
        }
        return codes;
    }

    private static string BuildNarrative(DecisionResult actual, Decision target, double baseline, double predicted, IReadOnlyList<FeatureContribution> contributions)
    {
        var top = contributions.Where(c => c.Direction != "Neutral").Take(3)
            .Select(c => $"{c.Feature}={c.Value} ({(c.Contribution >= 0 ? "+" : string.Empty)}{c.Contribution * 100:N1} pts)");
        return $"Decision {actual.Decision} with {actual.Confidence * 100:N1}% confidence. " +
               $"P({target}) moved from {baseline * 100:N1}% for a typical merchant to {predicted * 100:N1}%; " +
               $"main drivers: {string.Join(", ", top)}.";
    }
}
