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

### KYB & Compliance (`/api/kyb`)

Pre-boarding checks built entirely on free / public data sources. Everything works with no
API keys; OpenCorporates and UK Companies House are enabled automatically if a key is
configured (`Kyb:OpenCorporatesApiToken`, `Kyb:CompaniesHouseApiKey`).

| Endpoint | What it does | Sources |
|----------|--------------|---------|
| `POST verify-business` | Matches the declared legal name / address / registration number against corporate registries; flags `NEW_ENTITY`, `NAME_MISMATCH`, `REGISTERED_ADDRESS_MISMATCH`, `INACTIVE_ENTITY`, `VIRTUAL_OFFICE_ADDRESS`, `ENTITY_NOT_FOUND` | GLEIF LEI, SEC EDGAR, US Census geocoder, (OpenCorporates, Companies House) |
| `POST screen` | Fuzzy sanctions / PEP screening of the business and its beneficial owners (aliases, DOB, nationality aware) plus adverse-media search | OpenSanctions consolidated list (OFAC, EU, UN, UK HMT, …), OFAC SDN, UN Security Council, GDELT news |
| `GET screen/lists` | Status / row counts of the loaded sanctions lists | |
| `POST website-compliance` | Card-brand website requirements: TLS, privacy / terms / refund / delivery policies, contact details, currency, payment marks, checkout, legal-name disclosure, placeholder detection, domain age & expiry, prohibited content → score 0-100 and grade A-F | Site crawl, RDAP |
| `POST prohibited-business` | Classifies text / a business description against 23 prohibited, restricted and high-risk categories (CBD, crypto, adult, firearms, nutraceuticals, MLM, gambling, …) with MCC awareness | `Resources/restricted-categories.json` |
| `POST report` | Runs all of the above for one applicant and returns a combined risk tier and flag list | |

Sanctions lists are downloaded on first use into `Sanctions:CacheDirectory` (default
`data/sanctions`) and refreshed every `Sanctions:RefreshInterval` (24h). Set
`Sanctions:IncludePeps=true` to also load the (large) OpenSanctions PEP dataset. OpenSanctions
bulk data is CC BY-NC 4.0 – commercial use requires a licence from them; the OFAC and UN
lists are public domain.

### Underwriting (`/api/underwriting`)

Turns the credit model output plus the KYB signals into concrete terms an analyst can act on.
No external services are used; industry benchmarks are embedded (`Resources/industry-benchmarks.json`).

| Endpoint | What it does |
|----------|--------------|
| `POST explain` | Exact Shapley attribution of the credit decision over the six model features against a typical-merchant baseline, plus adverse-action reason codes (`HIGH_RISK_MCC`, `MATCH_LISTED`, `TICKET_SPREAD`, …) and a narrative |
| `POST recommend-terms` | Risk band A–E, rolling / capped / upfront reserve (%, days, cap, steady-state balance), interchange-plus markup, fees, settlement delay and volume caps, with the factors that drove them. Accepts optional KYB risk, website-compliance and plausibility scores, delivery days, CNP share, subscriptions / free trials |
| `POST volume-plausibility` | Checks declared annual volume / ticket sizes against industry ticket ranges, revenue-per-employee, tenure, prior-year revenue, bank-statement card deposits and catalogue size; flags e.g. `STARTUP_WITH_LARGE_VOLUME`, `DECLARED_FAR_ABOVE_STATEMENTS`, `ROUND_NUMBER_DECLARATION` |
| `POST bank-statement` (multipart `file`) / `POST bank-statement/csv` | Parses CSV or text-based PDF bank statements → monthly inflows / outflows, card-processor settlements (Stripe, Square, PayPal, Adyen, …) and implied annual card volume, NSF / overdrafts, returned items, loan payments, payroll, owner draws, negative-balance days, volatility and seasonality |
| `POST financial-statement` (multipart `file`) / `POST financial-statement/text` | Parses P&L / balance-sheet line items from CSV, text or PDF → gross & net margin, interest coverage, current ratio, leverage; flags `LOSS_MAKING`, `WEAK_DEBT_COVERAGE`, `ILLIQUID`, `NEGATIVE_EQUITY`, `CARD_VOLUME_EXCEEDS_REVENUE` |

Scanned / image-only PDFs are not OCR'd; the parser returns a warning instead of guessing.

## Project layout

```
src/
  MerchantIntelligence.CreditDecision/            # Credit model, training pipeline, predictor (ML.NET + LightGBM)
  MerchantIntelligence.CreditDecision.Trainer/    # Console app: trains and saves models/credit-decision.zip
  MerchantIntelligence.MccValidation/             # MCC catalog, SIC→MCC crosswalk, EDGAR client, scraper, classifier, providers
  MerchantIntelligence.MccValidation.DataPipeline/# Console app: downloads EDGAR data, trains models/mcc-classifier.zip
  MerchantIntelligence.Kyb/                       # Registry verification, sanctions screening, website compliance, prohibited-business taxonomy
  MerchantIntelligence.Underwriting/              # Decision explainability, reserve/pricing recommender, volume plausibility, statement parsing
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

### Run a KYB report

```bash
curl -X POST http://localhost:5292/api/kyb/report \
  -H 'content-type: application/json' \
  -d '{
    "business": { "legalName": "Apple Inc.", "addressLine": "One Apple Park Way", "city": "Cupertino",
                  "region": "CA", "postalCode": "95014", "country": "US", "websiteUrl": "https://www.apple.com" },
    "owners": [ { "fullName": "Tim Cook", "role": "CEO", "ownershipPercent": 0.01 } ],
    "declaredMcc": 5732
  }'
```

The first call downloads ~100 MB of sanctions data (30-60 s); subsequent calls are fast.

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
