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

### MCC Validation

Checks whether the merchant category code a business declares is consistent with what
its website actually sells. Several independent **evidence providers** each vote for MCCs
and an aggregator combines them into an explainable verdict:

| Provider | Evidence |
|----------|----------|
| Website keyword taxonomy | Deterministic keyword match of the site text against the MCC catalog (`Resources/mcc-catalog.json`) |
| ML text classifier | ML.NET TF-IDF + maximum-entropy classifier trained on SEC EDGAR 10-K "Item 1. Business" text and company websites, labelled via the SIC→MCC crosswalk (`Resources/sic-to-mcc.json`) |
| schema.org structured data | JSON-LD `@type` (Restaurant, Hotel, Pharmacy, …) mapped to MCCs |
| SEC EDGAR filer | If the domain belongs to a public filer, its SIC code mapped through the crosswalk |

`POST /api/mcc-validation/validate {"mcc": 5812, "websiteUrl": "https://example.com"}` returns
`verdict` (`Consistent` / `Questionable` / `Inconsistent` / `Insufficient`), `accuracyPercent`,
`suggestedMccs`, `riskFlags` (e.g. `HIDDEN_HIGH_RISK`, `MCC_MISMATCH`) and per-provider
`evidence`. `GET /api/mcc-validation/catalog` lists the MCC catalog for autocomplete.

An Angular 18 + Material front end lives in `web/mcc-validator`.

## Project layout

```
src/
  MerchantIntelligence.CreditDecision/            # Credit model, training pipeline, predictor (ML.NET + LightGBM)
  MerchantIntelligence.CreditDecision.Trainer/    # Console app: trains and saves models/credit-decision.zip
  MerchantIntelligence.MccValidation/             # MCC catalog, SIC→MCC crosswalk, EDGAR client, scraper, classifier, providers
  MerchantIntelligence.MccValidation.DataPipeline/# Console app: downloads EDGAR data, trains models/mcc-classifier.zip
  MerchantIntelligence.Api/                       # ASP.NET Core Web API (both tools)
web/
  mcc-validator/                                  # Angular UI for MCC validation
tests/
  MerchantIntelligence.Tests/                     # xUnit unit + integration tests
models/
  credit-decision.zip                             # Trained credit model
  mcc-classifier.zip                              # Trained MCC text classifier
  filer-domains.json                              # SEC filer domain → SIC index used by the EDGAR provider
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

### Validate an MCC

```bash
curl -X POST http://localhost:5292/api/mcc-validation/validate \
  -H 'content-type: application/json' \
  -d '{ "mcc": 5411, "websiteUrl": "https://www.draftkings.com" }'
```

The response includes `verdict`, `accuracyPercent`, `suggestedMccs`, `riskFlags` and the
evidence from every provider (including any that failed). Paths are configurable via
`MccValidation:ModelPath` and `MccValidation:FilerIndexPath`; if the classifier model is
missing the API still runs with the remaining providers.

### MCC validator UI

```bash
cd web/mcc-validator
npm install
npm start          # http://localhost:4200, proxies /api to the .NET API on :5292
npm run build      # production bundle in dist/
```

### Rebuilding the MCC classifier from SEC EDGAR

```bash
# Downloads company_tickers.json, per-company submissions (SIC, website), the latest 10-K
# "Item 1. Business" text and each company's homepage; caches everything under data/edgar/.
# SEC requires a descriptive User-Agent with contact details and <=10 req/s (the client throttles).
EDGAR_USER_AGENT="YourCompany you@example.com" \
  dotnet run -c Release --project src/MerchantIntelligence.MccValidation.DataPipeline -- download --limit 2000

# Train + evaluate (micro/macro accuracy, top-3) and save models/mcc-classifier.zip
dotnet run -c Release --project src/MerchantIntelligence.MccValidation.DataPipeline -- train
```

The pipeline writes `data/edgar/training.jsonl` (one `{text, mcc, source}` per line) — append
your own labelled merchant records to it before running `train` to improve coverage.

## Training data

No real underwriting history is included. `SyntheticDataGenerator` produces labelled
applications following common acquiring heuristics (high-risk MCCs, MATCH hits,
volume/ticket outliers, existing relationships) with added noise. Swap in real
decisions via `--data` to train a production model; the API and pipeline are unchanged.

The MCC classifier is trained on real public-company text from SEC EDGAR, but its labels are
**weak**: EDGAR reports SIC codes, which are mapped to MCCs through a curated crosswalk, and
the two taxonomies do not align one-to-one. EDGAR also skews towards large, finance, biotech
and technology filers, so small-merchant categories (restaurants, salons, local retail) are
thin; the catalog keyword provider and schema.org provider cover those until real
merchant-labelled data is added.
