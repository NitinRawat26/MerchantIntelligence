using Microsoft.ML.Data;

namespace MerchantIntelligence.CreditDecision;

public enum Decision
{
    Approved,
    Declined,
    Cancelled
}

/// <summary>Merchant application attributes used as model features.</summary>
public sealed class MerchantApplication
{
    [LoadColumn(0)] public float MerchantCategoryCode { get; set; }
    [LoadColumn(1)] public float AnnualVolume { get; set; }
    [LoadColumn(2)] public float AverageTicket { get; set; }
    [LoadColumn(3)] public float HighestTicket { get; set; }
    [LoadColumn(4)] public bool MatchFound { get; set; }
    [LoadColumn(5)] public bool ExistingRelationship { get; set; }
}

/// <summary>Training row: features plus the historical decision label.</summary>
public sealed class MerchantApplicationRecord
{
    [LoadColumn(0)] public float MerchantCategoryCode { get; set; }
    [LoadColumn(1)] public float AnnualVolume { get; set; }
    [LoadColumn(2)] public float AverageTicket { get; set; }
    [LoadColumn(3)] public float HighestTicket { get; set; }
    [LoadColumn(4)] public bool MatchFound { get; set; }
    [LoadColumn(5)] public bool ExistingRelationship { get; set; }
    [LoadColumn(6)] public string Decision { get; set; } = string.Empty;
}

public sealed class DecisionPrediction
{
    [ColumnName("PredictedLabel")] public string PredictedDecision { get; set; } = string.Empty;
    [ColumnName("Score")] public float[] Scores { get; set; } = [];
}

public sealed record DecisionResult(
    Decision Decision,
    double Confidence,
    IReadOnlyDictionary<Decision, double> Probabilities);
