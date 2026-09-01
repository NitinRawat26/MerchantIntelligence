namespace MerchantIntelligence.CreditDecision;

/// <summary>
/// Generates labelled merchant applications that follow typical acquiring
/// underwriting heuristics, with noise so the model must generalise rather than
/// memorise a rule table. Replace with real historical decisions when available.
/// </summary>
public static class SyntheticDataGenerator
{
    // High-risk MCCs (gambling, travel, telemarketing, dating, crypto-adjacent).
    private static readonly HashSet<int> HighRiskMccs =
        [4722, 4816, 5122, 5912, 5966, 5967, 5993, 6051, 6211, 7273, 7995, 5816, 7841];

    // Moderate-risk MCCs (electronics, jewellery, furniture, subscriptions).
    private static readonly HashSet<int> MediumRiskMccs =
        [5045, 5065, 5094, 5712, 5732, 5734, 5944, 5968, 5969, 7011, 7299, 7372, 8299];

    private static readonly int[] LowRiskMccs =
        [5411, 5499, 5541, 5812, 5814, 5912, 5921, 5942, 5943, 5977, 7230, 7538, 8011, 8021, 8043, 8062];

    public static IEnumerable<MerchantApplicationRecord> Generate(int count, int seed = 42)
    {
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var app = SampleApplication(rng);
            app.Decision = Label(app, rng).ToString();
            yield return app;
        }
    }

    private static MerchantApplicationRecord SampleApplication(Random rng)
    {
        var bucket = rng.NextDouble();
        int mcc = bucket switch
        {
            < 0.20 => HighRiskMccs.ElementAt(rng.Next(HighRiskMccs.Count)),
            < 0.45 => MediumRiskMccs.ElementAt(rng.Next(MediumRiskMccs.Count)),
            _ => LowRiskMccs[rng.Next(LowRiskMccs.Length)]
        };

        // Log-normal-ish annual volume between ~10k and ~50M.
        var annualVolume = (float)Math.Round(Math.Exp(rng.NextDouble() * 8.5 + 9.2), 2);
        var averageTicket = (float)Math.Round(Math.Exp(rng.NextDouble() * 6.5 + 1.5), 2);
        var highestTicket = (float)Math.Round(averageTicket * (1.5 + rng.NextDouble() * 40), 2);

        return new MerchantApplicationRecord
        {
            MerchantCategoryCode = mcc,
            AnnualVolume = annualVolume,
            AverageTicket = averageTicket,
            HighestTicket = highestTicket,
            MatchFound = rng.NextDouble() < 0.12,
            ExistingRelationship = rng.NextDouble() < 0.35
        };
    }

    private static Decision Label(MerchantApplicationRecord app, Random rng)
    {
        var risk = 0.0;
        var mcc = (int)app.MerchantCategoryCode;

        if (HighRiskMccs.Contains(mcc)) risk += 0.35;
        else if (MediumRiskMccs.Contains(mcc)) risk += 0.15;

        // MATCH (terminated merchant file) hit is close to a hard decline.
        if (app.MatchFound) risk += 0.45;

        if (app.ExistingRelationship) risk -= 0.20;

        if (app.AnnualVolume > 10_000_000) risk += 0.15;
        else if (app.AnnualVolume > 2_000_000) risk += 0.07;
        else if (app.AnnualVolume < 50_000) risk += 0.05;

        if (app.AverageTicket > 2_000) risk += 0.15;
        else if (app.AverageTicket > 500) risk += 0.07;

        var ticketRatio = app.HighestTicket / Math.Max(app.AverageTicket, 1f);
        if (ticketRatio > 25) risk += 0.12;
        else if (ticketRatio > 10) risk += 0.05;

        var impliedTxCount = app.AnnualVolume / Math.Max(app.AverageTicket, 1f);
        if (impliedTxCount < 50) risk += 0.10;

        risk += (rng.NextDouble() - 0.5) * 0.16;

        if (risk >= 0.45) return Decision.Declined;

        // Borderline applications are sometimes withdrawn / cancelled by the merchant
        // (e.g. asked for additional documentation or a reserve and walked away).
        if (risk >= 0.25 && rng.NextDouble() < 0.55) return Decision.Cancelled;
        if (rng.NextDouble() < 0.04) return Decision.Cancelled;

        return Decision.Approved;
    }
}
