using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.Text;

namespace MerchantIntelligence.MccValidation.Classification;

/// <summary>One labelled training example: free text about a business plus its MCC.</summary>
public sealed class MccTrainingRecord
{
    public string Text { get; set; } = string.Empty;
    public string Mcc { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public sealed record MccScore(int Mcc, float Probability);

public sealed record MccTextPrediction(IReadOnlyList<MccScore> Ranked)
{
    public MccScore Top => Ranked[0];

    public float ProbabilityOf(int mcc) => Ranked.FirstOrDefault(r => r.Mcc == mcc)?.Probability ?? 0f;
}

public sealed record MccModelMetrics(double MicroAccuracy, double MacroAccuracy, double LogLoss, double Top3Accuracy, int ClassCount, int TestRows);

/// <summary>
/// TF-IDF n-gram text classifier over MCC labels.
/// </summary>
public sealed class MccTextClassifier
{
    private const string LabelColumn = "Label";
    private const string FeaturesColumn = "Features";

    private readonly MLContext _ml;
    private readonly ITransformer _model;
    private readonly int[] _classOrder;
    private readonly PredictionEnginePool _pool;

    private MccTextClassifier(MLContext ml, ITransformer model, DataViewSchema inputSchema)
    {
        _ml = ml;
        _model = model;
        var outputSchema = model.GetOutputSchema(inputSchema);
        _classOrder = ReadClassOrder(outputSchema);
        _pool = new PredictionEnginePool(() => _ml.Model.CreatePredictionEngine<MccTrainingRecord, RawPrediction>(_model, inputSchema));
    }

    public int ClassCount => _classOrder.Length;

    public MccTextPrediction Predict(string text, int topK = 5)
    {
        var raw = _pool.Run(engine => engine.Predict(new MccTrainingRecord { Text = text }));
        var ranked = raw.Score
            .Select((p, i) => new MccScore(_classOrder[i], p))
            .OrderByDescending(s => s.Probability)
            .Take(topK)
            .ToList();
        return new MccTextPrediction(ranked);
    }

    public static IEstimator<ITransformer> BuildPipeline(MLContext ml)
    {
        var textOptions = new TextFeaturizingEstimator.Options
        {
            CaseMode = TextNormalizingEstimator.CaseMode.Lower,
            KeepDiacritics = false,
            KeepPunctuations = false,
            KeepNumbers = false,
            StopWordsRemoverOptions = new StopWordsRemovingEstimator.Options(),
            WordFeatureExtractor = new WordBagEstimator.Options { NgramLength = 2, UseAllLengths = true, Weighting = NgramExtractingEstimator.WeightingCriteria.TfIdf, MaximumNgramsCount = new[] { 50_000, 50_000 } },
            CharFeatureExtractor = null,
            Norm = TextFeaturizingEstimator.NormFunction.L2
        };

        return ml.Transforms.Conversion.MapValueToKey(LabelColumn, nameof(MccTrainingRecord.Mcc))
            .Append(ml.Transforms.Text.FeaturizeText(FeaturesColumn, textOptions, nameof(MccTrainingRecord.Text)))
            .Append(ml.MulticlassClassification.Trainers.SdcaMaximumEntropy(LabelColumn, FeaturesColumn, l2Regularization: 0.01f, maximumNumberOfIterations: 60))
            .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
    }

    public static (MccTextClassifier Classifier, MccModelMetrics Metrics) Train(IEnumerable<MccTrainingRecord> records, double testFraction = 0.2, int seed = 42)
    {
        var ml = new MLContext(seed);
        var data = ml.Data.LoadFromEnumerable(records);
        var split = ml.Data.TrainTestSplit(data, testFraction, samplingKeyColumnName: null, seed: seed);

        var model = BuildPipeline(ml).Fit(split.TrainSet);
        var classifier = new MccTextClassifier(ml, model, data.Schema);

        var scored = model.Transform(split.TestSet);
        var evaluation = ml.MulticlassClassification.Evaluate(scored, LabelColumn, topKPredictionCount: 3);
        var metrics = new MccModelMetrics(
            evaluation.MicroAccuracy,
            evaluation.MacroAccuracy,
            evaluation.LogLoss,
            evaluation.TopKAccuracyForAllK?.Count >= 3 ? evaluation.TopKAccuracyForAllK[2] : evaluation.TopKAccuracy,
            classifier.ClassCount,
            (int)(scored.GetRowCount() ?? ml.Data.CreateEnumerable<RawPrediction>(scored, reuseRowObject: true).Count()));

        return (classifier, metrics);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var schema = _ml.Data.LoadFromEnumerable(Array.Empty<MccTrainingRecord>()).Schema;
        _ml.Model.Save(_model, schema, path);
    }

    public static MccTextClassifier Load(string path)
    {
        var ml = new MLContext();
        var model = ml.Model.Load(path, out var schema);
        return new MccTextClassifier(ml, model, schema);
    }

    private static int[] ReadClassOrder(DataViewSchema schema)
    {
        VBuffer<ReadOnlyMemory<char>> names = default;
        schema["Score"].GetSlotNames(ref names);
        return names.DenseValues().Select(n => int.Parse(n.ToString())).ToArray();
    }

    private sealed class RawPrediction
    {
        public string PredictedLabel { get; set; } = string.Empty;
        public float[] Score { get; set; } = Array.Empty<float>();
    }

    /// <summary>PredictionEngine is not thread-safe; keep a small pool.</summary>
    private sealed class PredictionEnginePool
    {
        private readonly Func<PredictionEngine<MccTrainingRecord, RawPrediction>> _factory;
        private readonly System.Collections.Concurrent.ConcurrentBag<PredictionEngine<MccTrainingRecord, RawPrediction>> _engines = new();

        public PredictionEnginePool(Func<PredictionEngine<MccTrainingRecord, RawPrediction>> factory) => _factory = factory;

        public T Run<T>(Func<PredictionEngine<MccTrainingRecord, RawPrediction>, T> action)
        {
            if (!_engines.TryTake(out var engine)) engine = _factory();
            try { return action(engine); }
            finally { _engines.Add(engine); }
        }
    }
}
