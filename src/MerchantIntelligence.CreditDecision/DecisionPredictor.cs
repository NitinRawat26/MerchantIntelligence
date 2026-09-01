using Microsoft.ML;
using Microsoft.ML.Data;

namespace MerchantIntelligence.CreditDecision;

public interface IDecisionPredictor
{
    DecisionResult Predict(MerchantApplication application);
}

/// <summary>Thread-safe wrapper around a trained ML.NET model.</summary>
public sealed class DecisionPredictor : IDecisionPredictor
{
    private readonly PredictionEngine<MerchantApplicationRecord, DecisionPrediction> _engine;
    private readonly Decision[] _classOrder;
    private readonly object _lock = new();

    public DecisionPredictor(MLContext ml, ITransformer model)
    {
        _engine = ml.Model.CreatePredictionEngine<MerchantApplicationRecord, DecisionPrediction>(model);
        _classOrder = ReadClassOrder(_engine.OutputSchema);
    }

    public static DecisionPredictor Load(string modelPath)
    {
        var ml = new MLContext(seed: 42);
        var model = ml.Model.Load(modelPath, out _);
        return new DecisionPredictor(ml, model);
    }

    public DecisionResult Predict(MerchantApplication application)
    {
        var input = new MerchantApplicationRecord
        {
            MerchantCategoryCode = application.MerchantCategoryCode,
            AnnualVolume = application.AnnualVolume,
            AverageTicket = application.AverageTicket,
            HighestTicket = application.HighestTicket,
            MatchFound = application.MatchFound,
            ExistingRelationship = application.ExistingRelationship
        };

        DecisionPrediction prediction;
        lock (_lock)
        {
            prediction = _engine.Predict(input);
        }

        var probabilities = new Dictionary<Decision, double>();
        for (var i = 0; i < _classOrder.Length && i < prediction.Scores.Length; i++)
        {
            probabilities[_classOrder[i]] = Math.Round(prediction.Scores[i], 4);
        }

        var decision = Enum.Parse<Decision>(prediction.PredictedDecision);
        return new DecisionResult(decision, probabilities[decision], probabilities);
    }

    private static Decision[] ReadClassOrder(DataViewSchema schema)
    {
        var scoreColumn = schema["Score"];
        VBuffer<ReadOnlyMemory<char>> slotNames = default;
        scoreColumn.GetSlotNames(ref slotNames);
        return slotNames.DenseValues()
            .Select(n => Enum.Parse<Decision>(n.ToString()))
            .ToArray();
    }
}
