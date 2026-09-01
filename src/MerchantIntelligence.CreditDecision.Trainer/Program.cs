using MerchantIntelligence.CreditDecision;
using Microsoft.ML;

// Usage: dotnet run -- [outputPath] [--data <csv>] [--rows <n>]
var outputPath = "models/credit-decision.zip";
string? dataPath = null;
var rows = 20_000;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data": dataPath = args[++i]; break;
        case "--rows": rows = int.Parse(args[++i]); break;
        default: outputPath = args[i]; break;
    }
}

var ml = new MLContext(seed: 42);

List<MerchantApplicationRecord> records;
if (dataPath is not null)
{
    Console.WriteLine($"Loading training data from {dataPath}");
    var view = ml.Data.LoadFromTextFile<MerchantApplicationRecord>(dataPath, hasHeader: true, separatorChar: ',');
    records = ml.Data.CreateEnumerable<MerchantApplicationRecord>(view, reuseRowObject: false).ToList();
}
else
{
    Console.WriteLine($"Generating {rows} synthetic applications");
    records = SyntheticDataGenerator.Generate(rows).ToList();
}

foreach (var group in records.GroupBy(r => r.Decision).OrderBy(g => g.Key))
{
    Console.WriteLine($"  {group.Key,-10} {group.Count(),7} ({100.0 * group.Count() / records.Count:F1}%)");
}

Console.WriteLine("Training LightGBM multiclass model...");
var (model, metrics) = ModelTrainer.Train(ml, records);

Console.WriteLine($"MacroAccuracy: {metrics.MacroAccuracy:P2}");
Console.WriteLine($"MicroAccuracy: {metrics.MicroAccuracy:P2}");
Console.WriteLine($"LogLoss:       {metrics.LogLoss:F4}");
Console.WriteLine(metrics.ConfusionMatrix);

ModelTrainer.Save(ml, model, records.Take(1), outputPath);
Console.WriteLine($"Model saved to {Path.GetFullPath(outputPath)}");
