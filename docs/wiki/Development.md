# Development

## Prerequisites

* .NET 10 SDK (`dotnet --list-sdks` must show 10.0.x; on macOS Arm64 use the installer `.pkg`).
* Node 20+ and npm for `web/workbench`.
* ~1 GB free disk for NuGet packages, the sanctions cache and EDGAR data if you retrain.

Key packages: `Microsoft.Agents.AI.Workflows` (workflow graph), `Microsoft.ML` + LightGBM (credit
model), `Microsoft.ML` TF-IDF (MCC classifier), `HtmlAgilityPack` (HTML extraction),
`Microsoft.Data.Sqlite`, `QuestPDF` (memo), `PdfPig` (statement PDFs).

## Build, test, run

```bash
dotnet restore
dotnet build --no-restore -warnaserror        # CI builds with warnings as errors
dotnet test --no-build                        # xUnit: unit + WebApplicationFactory integration tests

dotnet run --project src/MerchantIntelligence.Api          # http://localhost:5292, Swagger at /swagger

cd web/workbench && npm ci && npm start                    # http://localhost:4200 (proxies /api → :5292)
```

The API needs `models/credit-decision.zip` at start-up. It is committed; to regenerate:

```bash
dotnet run --project src/MerchantIntelligence.CreditDecision.Trainer -- models/credit-decision.zip
dotnet run --project src/MerchantIntelligence.CreditDecision.Trainer -- models/credit-decision.zip --data history.csv
```

First KYB screening call downloads ~100 MB of sanctions data (30–60 s) into `data/sanctions`.

## Tests

`tests/MerchantIntelligence.Tests/`:

| File | Covers |
|------|--------|
| `WorkflowTests.cs` | Planner validation, stage computation, runner ordering/skip semantics, repository versions |
| `AssessmentApiTests.cs` | End-to-end `/api/assessment` incl. streaming and agents |
| `PlatformTests.cs`, `PlatformApiTests.cs` | Score, rules, cases, audit chain, webhooks, model ops |
| `KybTests.cs`, `KybApiTests.cs` | Name matching, prohibited detector, compliance scanner, verification |
| `MccValidationTests.cs`, `MccValidationApiTests.cs` | Extractor, providers, aggregator |
| `UnderwritingTests.cs`, `UnderwritingApiTests.cs` | Explainer, pricing, plausibility, statement parsers |
| `DecisionPredictorTests.cs`, `CreditDecisionApiTests.cs` | Model load and predict |

Integration tests use `Platform:DatabasePath=:memory:` and stub external HTTP where needed; tests
that reach public sources tolerate *Unavailable* results.

Run a subset: `dotnet test --filter FullyQualifiedName~WorkflowTests`.

## CI

`.github/workflows/ci.yml`: on push/PR — `dotnet restore`, `dotnet build -warnaserror`,
`dotnet test`; separately `npm ci && npm run build` in `web/workbench` on Node 20.
Render auto-deploys the `base` branch via `render.yaml`.

## Repository conventions

* Domain projects expose `Add<Module>()` DI extensions; controllers stay thin.
* Findings and flags are SCREAMING_SNAKE codes with a human message and severity so the UI and PDF
  can render them uniformly.
* Anything unavailable is reported as such — never inferred as clear (see [Data sources](Data-Sources.md#interpreting-absence)).
* Workflow and rules changes are data; keep `default-workflow.json` / `default-rules.json` valid
  against the planner/engine (tests assert this).
* Agent set is fixed at four; add capability by adding **steps** ([Agents and steps](Agents-and-Steps.md#adding-a-step)).

## Repo skills (`.agents/skills/`)

Reusable manual acceptance procedures used by automated sessions and reviewers:

| Skill | What it verifies |
|-------|------------------|
| `suite-ui-testing` | Angular workbench against the local API: stateful browser flows |
| `kyb-api-testing` | Live KYB endpoints through Swagger/curl, honouring public-source limits |
| `underwriting-api-testing` | Explanations, terms and statement uploads with deterministic fixtures |

## Retraining the MCC classifier

See [MCC validation → Retraining](MCC-Validation.md#retraining-the-edgar-classifier).

## Documentation

* `README.md` — overview and quick start.
* `docs/full-assessment.md` — functional specification of the full assessment.
* `docs/wiki/` — this wiki (architecture and module reference). Keep pages in sync when adding
  steps, endpoints, settings or finding codes.
