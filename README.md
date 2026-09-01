# MerchantIntelligence

A suite of tools for merchant acquiring pre-checks and credit checks, built in C# / .NET 8.

## Tools

### Credit Decision Prediction Engine

An ML.NET **LightGBM** multiclass model that predicts the underwriting outcome of a
merchant application — `Approved`, `Declined` or `Cancelled` — together with the
probability (%) of each outcome.

**Input features**

| Field                  | Type    | Description                                   |
|------------------------|---------|-----------------------------------------------|
| `merchantCategoryCode` | int     | MCC (e.g. 5411 grocery, 7995 gambling)        |
| `annualVolume`         | decimal | Projected annual card volume                  |
| `averageTicket`        | decimal | Average transaction amount                    |
| `highestTicket`        | decimal | Highest expected single transaction           |
| `matchFound`           | bool    | Hit on the MATCH / terminated merchant file   |
| `existingRelationship` | bool    | Merchant already banks / processes with us    |

## Project layout

```
src/
  MerchantIntelligence.CreditDecision/         # Model, training pipeline, predictor (ML.NET + LightGBM)
  MerchantIntelligence.CreditDecision.Trainer/ # Console app: trains and saves models/credit-decision.zip
  MerchantIntelligence.Api/                    # ASP.NET Core Web API
tests/
  MerchantIntelligence.Tests/                  # xUnit unit + integration tests
models/
  credit-decision.zip                          # Trained model consumed by the API
```

## Getting started

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Train (or retrain) the model. Uses synthetic data by default.
dotnet run --project src/MerchantIntelligence.CreditDecision.Trainer -- models/credit-decision.zip

# Train on your own historical decisions (CSV, header row, columns in the order
# MerchantCategoryCode,AnnualVolume,AverageTicket,HighestTicket,MatchFound,ExistingRelationship,Decision)
dotnet run --project src/MerchantIntelligence.CreditDecision.Trainer -- models/credit-decision.zip --data history.csv

# Run tests
dotnet test

# Run the API (Swagger UI at /swagger)
dotnet run --project src/MerchantIntelligence.Api
```

### Predict

```bash
curl -X POST http://localhost:5292/api/credit-decision/predict \
  -H 'content-type: application/json' \
  -d '{
    "merchantCategoryCode": 5411,
    "annualVolume": 800000,
    "averageTicket": 40,
    "highestTicket": 250,
    "matchFound": false,
    "existingRelationship": true
  }'
```

```json
{
  "decision": "Approved",
  "confidencePercent": 96.04,
  "probabilitiesPercent": { "Approved": 96.04, "Cancelled": 3.94, "Declined": 0.02 }
}
```

The model path can be overridden with the `CreditDecision:ModelPath` setting
(e.g. `CreditDecision__ModelPath=/models/prod.zip`).

## Training data

No real underwriting history is included. `SyntheticDataGenerator` produces labelled
applications following common acquiring heuristics (high-risk MCCs, MATCH hits,
volume/ticket outliers, existing relationships) with added noise. Swap in real
decisions via `--data` to train a production model; the API and pipeline are unchanged.
