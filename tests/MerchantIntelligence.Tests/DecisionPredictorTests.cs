using MerchantIntelligence.CreditDecision;
using Microsoft.ML;

namespace MerchantIntelligence.Tests;

public sealed class DecisionPredictorTests
{
    private static readonly Lazy<DecisionPredictor> Predictor = new(() =>
    {
        var ml = new MLContext(seed: 42);
        var (model, _) = ModelTrainer.Train(ml, SyntheticDataGenerator.Generate(5_000));
        return new DecisionPredictor(ml, model);
    });

    [Fact]
    public void Training_reaches_reasonable_accuracy()
    {
        var ml = new MLContext(seed: 42);
        var (_, metrics) = ModelTrainer.Train(ml, SyntheticDataGenerator.Generate(5_000));
        Assert.True(metrics.MicroAccuracy > 0.7, $"MicroAccuracy was {metrics.MicroAccuracy}");
    }

    [Fact]
    public void Probabilities_sum_to_one_and_cover_all_decisions()
    {
        var result = Predictor.Value.Predict(new MerchantApplication
        {
            MerchantCategoryCode = 5411, AnnualVolume = 500_000, AverageTicket = 45, HighestTicket = 300
        });

        Assert.Equal(3, result.Probabilities.Count);
        Assert.InRange(result.Probabilities.Values.Sum(), 0.99, 1.01);
        Assert.Equal(result.Probabilities[result.Decision], result.Confidence);
        Assert.Equal(result.Probabilities.Values.Max(), result.Confidence);
    }

    [Fact]
    public void Low_risk_grocery_merchant_with_relationship_is_approved()
    {
        var result = Predictor.Value.Predict(new MerchantApplication
        {
            MerchantCategoryCode = 5411, AnnualVolume = 800_000, AverageTicket = 40, HighestTicket = 250,
            MatchFound = false, ExistingRelationship = true
        });
        Assert.Equal(Decision.Approved, result.Decision);
    }

    [Fact]
    public void High_risk_mcc_with_match_hit_is_declined()
    {
        var result = Predictor.Value.Predict(new MerchantApplication
        {
            MerchantCategoryCode = 7995, AnnualVolume = 15_000_000, AverageTicket = 2_500, HighestTicket = 90_000,
            MatchFound = true, ExistingRelationship = false
        });
        Assert.Equal(Decision.Declined, result.Decision);
    }

    [Fact]
    public void Existing_relationship_outweighs_high_risk_mcc()
    {
        var withoutRelationship = Predictor.Value.Predict(new MerchantApplication
        {
            MerchantCategoryCode = 7995, AnnualVolume = 3_000_000, AverageTicket = 600, HighestTicket = 8_000,
            MatchFound = false, ExistingRelationship = false
        });
        var withRelationship = Predictor.Value.Predict(new MerchantApplication
        {
            MerchantCategoryCode = 7995, AnnualVolume = 3_000_000, AverageTicket = 600, HighestTicket = 8_000,
            MatchFound = false, ExistingRelationship = true
        });

        Assert.NotEqual(Decision.Approved, withoutRelationship.Decision);
        Assert.Equal(Decision.Approved, withRelationship.Decision);
        Assert.True(withRelationship.Probabilities[Decision.Approved] > withoutRelationship.Probabilities[Decision.Approved] + 0.5);
    }

    [Fact]
    public void Save_and_load_round_trips()
    {
        var ml = new MLContext(seed: 42);
        var records = SyntheticDataGenerator.Generate(2_000).ToList();
        var (model, _) = ModelTrainer.Train(ml, records);
        var path = Path.Combine(Path.GetTempPath(), $"mi-{Guid.NewGuid():N}.zip");
        try
        {
            ModelTrainer.Save(ml, model, records.Take(1), path);
            var loaded = DecisionPredictor.Load(path);
            var app = new MerchantApplication { MerchantCategoryCode = 5812, AnnualVolume = 300_000, AverageTicket = 30, HighestTicket = 200 };
            Assert.Equal(new DecisionPredictor(ml, model).Predict(app).Decision, loaded.Predict(app).Decision);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
