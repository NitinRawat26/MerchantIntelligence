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

The MCC validator is one page of the Angular workspace in `web/mcc-validator` (see [Web UI](#web-ui)).

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

### Platform (`/api/platform`)

Ties the tools together into one decision, a policy layer, an analyst queue and model governance.
State lives in a local SQLite file (`Platform:DatabasePath`, default `data/platform.db`; `:memory:` for tests).

| Area | Endpoints | What it does |
|------|-----------|--------------|
| Unified risk score | `POST score` | Combines credit model, KYB, sanctions/PEP/adverse media, prohibited-business verdict, website compliance, volume plausibility and pricing band into one 0–1000 score, tier, recommended action and reason codes. Sections not supplied are reported as `coverageGaps`; sanctions, prohibited business and MATCH are hard stops. Evaluates the active rule set and can open a case in the same call |
| Rules engine | `GET rules`, `POST rules/validate`, `POST rules/publish`, `GET rules/history`, `GET rules/{v}`, `POST rules/rollback/{v}`, `POST rules/evaluate` | JSON policy rules (`all` / `any` / `not` trees; `eq neq gt gte lt lte contains notcontains in exists`) with Approve / Refer / Decline outcomes, most severe wins. Versioned, audited, roll-backable; an embedded default set ships with the suite |
| Case management | `POST cases`, `GET cases`, `GET cases/stats`, `GET cases/{id}`, `…/assign`, `…/status`, `…/notes`, `…/decide`, `…/audit` | Review queue with priorities, assignment, notes and terminal decisions. A decision that contradicts the rules outcome is an override and needs a reason. Every mutation is audited and raises a webhook |
| Audit trail | `GET audit`, `GET audit/verify` | Append-only, SHA-256 hash-chained event log; `verify` walks the chain and reports the first tampered sequence number |
| Webhooks | `POST/GET webhooks`, `DELETE webhooks/{id}`, `GET webhooks/deliveries`, `GET webhooks/events` | HTTPS subscribers for `case.*`, `rules.published`, `model.promoted`, `model.drift_alert`. Payloads are signed (`X-MI-Signature: sha256=HMAC(secret, body)`), retried with back-off, and every attempt is persisted |
| Model ops | `GET models`, `GET models/decisions`, `POST models/decisions/{id}/outcome`, `GET models/drift`, `GET models/compare`, `POST models/retrain`, `POST models/promote` | Every prediction is logged (champion + shadow challenger). Record realised outcomes, get PSI drift per feature and on the prediction mix, compare champion vs challenger accuracy, retrain on labelled decisions topped up with synthetic rows, and promote without a restart |
| MATCH boundary | `POST match/inquiry` | Mastercard MATCH requires acquirer credentials, so by default this returns `availability: NotConfigured` / `found: null` (unknown, never "clear"). Point `Match:Endpoint` at a MATCH-compatible service or `Match:LocalListPath` at your own terminated-merchant CSV to get real hits |

### Full assessment (`/api/assessment`, UI `/assess`)

One intake, every check, one decision. Collects the business, owners, website, MCC, declared volumes and optional statements once, then runs the checks in order — verification → screening → website → prohibited/restricted → MCC → MATCH → bank statement → P&L → volume plausibility → credit model + explainability → terms → unified score + rules → case — and returns a persisted result with a detailed explainability report and a PDF.

| Endpoint | What it does |
|----------|--------------|
| `GET steps` | Ordered step catalogue (id + display name) |
| `POST run` | Runs everything, returns the `AssessmentResult`. Body is JSON, or `multipart/form-data` with a `request` JSON part plus optional `bankStatement` (CSV/PDF) and `financialStatement` (text/PDF) files |
| `POST run/stream` | Same input; responds with newline-delimited JSON: `{"type":"steps"}` once, `{"type":"step"}` per status change (Pending/Running/Succeeded/Failed/Skipped), then `{"type":"result"}` |
| `GET`, `GET {id}` | History and stored results |
| `GET {id}/pdf` | Full underwriting report (decision, intake, check outcomes, score components, reason codes, model contributions, rules, narrative, findings, terms, analyst actions, execution log, source limitations) |

Each step is isolated: a failing step is recorded as a coverage gap, never as clear. If every public registry fails, or no sanctions list could be downloaded, the result is reported as *Unavailable* and excluded from the unified score rather than being read as verified/clear. MATCH stays `NotConfigured` without credentials.

```bash
curl -s localhost:5292/api/assessment/run -H 'Content-Type: application/json' -d '{
  "business":{"legalName":"Shopify Inc.","country":"CA","websiteUrl":"https://www.shopify.com"},
  "owners":[{"fullName":"Tobias Lutke"}],
  "businessDescription":"E-commerce platform","merchantCategoryCode":5734,
  "annualVolume":1000000,"averageTicket":50,"highestTicket":500}' | jq .decision
curl -s localhost:5292/api/assessment/ASMT-.../pdf -o report.pdf
```

## Project layout

```
src/
  MerchantIntelligence.CreditDecision/            # Credit model, training pipeline, predictor (ML.NET + LightGBM)
  MerchantIntelligence.CreditDecision.Trainer/    # Console app: trains and saves models/credit-decision.zip
  MerchantIntelligence.MccValidation/             # MCC catalog, SIC→MCC crosswalk, EDGAR client, scraper, classifier, providers
  MerchantIntelligence.MccValidation.DataPipeline/# Console app: downloads EDGAR data, trains models/mcc-classifier.zip
  MerchantIntelligence.Kyb/                       # Registry verification, sanctions screening, website compliance, prohibited-business taxonomy
  MerchantIntelligence.Underwriting/              # Decision explainability, reserve/pricing recommender, volume plausibility, statement parsing
  MerchantIntelligence.Platform/                  # Unified score, rules engine, cases + audit (SQLite), webhooks, model ops, MATCH boundary
  MerchantIntelligence.Api/                       # ASP.NET Core Web API (all tools)
web/
  mcc-validator/                                  # Angular 18 + Material UI for the whole suite
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

## Web UI

`web/mcc-validator` is an Angular 18 + Material single-page app with a page per capability. All pages
call the .NET API through the `/api` dev proxy (`proxy.conf.json` → `http://localhost:5292`).

| Route | Page |
|-------|------|
| `/assess`, `/assess/:id` | Full assessment (default page): one intake form (business, owners, website, MCC, volumes, statements/uploads), live step-by-step run, decision card, tabbed explainability report (identity & screening, website/MCC/business type, financials & plausibility, terms, run log), PDF download and recent-assessment history |
| `/score` | Unified risk score: enter credit application + upstream KYB/screening/website/plausibility signals, see score, tier, coverage gaps, hard stops, reason codes, matched rules; optionally open a case |
| `/kyb` | KYB & screening: business identity, beneficial owners, registry sources, sanctions/PEP/adverse media, website compliance checks, prohibited-business verdict |
| `/underwriting` | Explainability (Shapley bars + reason codes), reserve & pricing terms, volume plausibility, bank-statement CSV/PDF and P&L analysis |
| `/mcc` | MCC validator (unchanged) |
| `/cases`, `/cases/:id` | Analyst queue with stats/filters; case detail with assign, status, notes, decide (override reason enforced) and per-case audit trail |
| `/rules` | Active rule-set JSON editor with validate / publish / evaluate against sample facts, version history and rollback |
| `/audit` | Recent audit events and hash-chain verification |
| `/models` | Model registry, champion/challenger comparison, drift report, decision log with outcome labelling, retrain/promote |
| `/match` | MATCH inquiry; shows `NotConfigured` explicitly when no local list / endpoint is configured |
| `/webhooks` | Register subscriptions (secret never echoed), list deliveries and attempts |

Unit tests run headless with `CHROME_BIN=<chrome> npx ng test --watch=false --browsers=ChromeHeadless`.

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
