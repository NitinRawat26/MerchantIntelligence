using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.LightGbm;

namespace MerchantIntelligence.CreditDecision;

public sealed record TrainingMetrics(
    double MacroAccuracy,
    double MicroAccuracy,
    double LogLoss,
    string ConfusionMatrix);

public static class ModelTrainer
{
    public const string FeaturesColumn = "Features";
    public const string LabelColumn = "Label";

    public static IEstimator<ITransformer> BuildPipeline(MLContext ml)
    {
        var options = new LightGbmMulticlassTrainer.Options
        {
            LabelColumnName = LabelColumn,
            FeatureColumnName = FeaturesColumn,
            NumberOfLeaves = 31,
            MinimumExampleCountPerLeaf = 20,
            NumberOfIterations = 200,
            LearningRate = 0.05,
            Seed = 42
        };

        return ml.Transforms.Conversion.MapValueToKey(LabelColumn, nameof(MerchantApplicationRecord.Decision))
            .Append(ml.Transforms.Conversion.ConvertType(
                new[]
                {
                    new InputOutputColumnPair("MatchFoundF", nameof(MerchantApplicationRecord.MatchFound)),
                    new InputOutputColumnPair("ExistingRelationshipF", nameof(MerchantApplicationRecord.ExistingRelationship))
                },
                DataKind.Single))
            .Append(ml.Transforms.Concatenate(
                FeaturesColumn,
                nameof(MerchantApplicationRecord.MerchantCategoryCode),
                nameof(MerchantApplicationRecord.AnnualVolume),
                nameof(MerchantApplicationRecord.AverageTicket),
                nameof(MerchantApplicationRecord.HighestTicket),
                "MatchFoundF",
                "ExistingRelationshipF"))
            .Append(ml.MulticlassClassification.Trainers.LightGbm(options))
            .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
    }

    public static (ITransformer Model, TrainingMetrics Metrics) Train(
        MLContext ml,
        IEnumerable<MerchantApplicationRecord> records,
        double testFraction = 0.2)
    {
        var data = ml.Data.LoadFromEnumerable(records);
        var split = ml.Data.TrainTestSplit(data, testFraction, seed: 42);

        var model = BuildPipeline(ml).Fit(split.TrainSet);

        var predictions = model.Transform(split.TestSet);
        var m = ml.MulticlassClassification.Evaluate(predictions, LabelColumn);

        var metrics = new TrainingMetrics(
            m.MacroAccuracy,
            m.MicroAccuracy,
            m.LogLoss,
            m.ConfusionMatrix.GetFormattedConfusionTable());

        return (model, metrics);
    }

    public static void Save(MLContext ml, ITransformer model, IEnumerable<MerchantApplicationRecord> sample, string path)
    {
        var schema = ml.Data.LoadFromEnumerable(sample).Schema;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        ml.Model.Save(model, schema, path);
    }
}
