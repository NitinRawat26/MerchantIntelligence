# API reference

Base URL: `http://localhost:5292` (dev) or the deployed host. Interactive docs: `/swagger`.
Health: `GET /health`. All bodies are JSON unless marked *multipart*. Request/response shapes are
defined in the controllers under `src/MerchantIntelligence.Api/Controllers/`.

## Full assessment — `/api/assessment` (`AssessmentController`)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `steps` | Step catalogue of the active workflow (id, display name, enabled) |
| GET | `agents` | Agent catalogue of the active workflow (id, name, mandate, enabled, owned steps) |
| POST | `run` | Run the active workflow; returns `AssessmentResult`. Body: `AssessmentIntake` JSON, or *multipart* with a `request` JSON part plus optional `bankStatement` (CSV/PDF) and `financialStatement` (text/PDF) files |
| POST | `run/stream` | Same input; NDJSON response: `{"type":"steps",…}` once, `{"type":"step",…}` per status change, `{"type":"agent",…}` on agent start/finish, `{"type":"result",…}` last |
| GET | `` | Recent assessments |
| GET | `{id}` | Stored result |
| GET | `{id}/pdf` | Underwriting memo PDF |

## Workflows — `/api/workflows` (`WorkflowController`)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `catalog` | Step descriptors: id, name, description, `dependsOn`, `consumes`, `required`, param schema |
| GET | `agents` | Agent descriptors: id, name, mandate, description, default steps |
| GET | `default` | Embedded default workflow |
| GET | `active` · `active/plan` · `active/steps` | Active definition, its plan (stages, agents, warnings, Mermaid) and step list |
| GET | `history` · `{version}` | Version list; one stored version |
| POST | `validate` | Validate a draft → plan + warnings (not saved) |
| POST | `publish` | Store a new version and activate it (`author`, `comment`; audited) |
| POST | `rollback/{version}` | Re-activate an old version as a new version |

## Credit decision — `/api/credit-decision`

| Method | Path | Purpose |
|--------|------|---------|
| POST | `predict` | `{merchantCategoryCode, annualVolume, averageTicket, highestTicket, matchFound, existingRelationship}` → decision, confidence, per-class probabilities |

## MCC validation — `/api/mcc-validation`

| Method | Path | Purpose |
|--------|------|---------|
| POST | `validate` | `{mcc, websiteUrl}` → verdict, accuracyPercent, suggestedMccs, riskFlags, per-provider evidence |
| GET | `catalog` | MCC catalog for autocomplete |

## KYB — `/api/kyb`

| Method | Path | Purpose |
|--------|------|---------|
| POST | `verify-business` | Registry verification + local presence |
| POST | `screen` | Sanctions / PEP / adverse-media screening of business and owners |
| GET | `screen/lists` | Loaded sanctions lists and row counts |
| POST | `website-compliance` | Card-brand website checks → score, grade, per-check table, RDAP record |
| POST | `prohibited-business` | Classify description/text (+ MCC) against restricted categories |
| GET | `prohibited-business/categories` | The category taxonomy |
| POST | `report` | All of the above for one applicant → combined risk tier and flags |

## Underwriting — `/api/underwriting`

| Method | Path | Purpose |
|--------|------|---------|
| POST | `explain` | Shapley attribution + adverse-action reason codes + narrative |
| POST | `recommend-terms` | Band A–E, reserve, pricing, settlement delay, caps, factors |
| POST | `volume-plausibility` | Plausibility score and flags |
| POST | `bank-statement` (*multipart* `file`) · `bank-statement/csv` | Cash-flow analysis |
| POST | `financial-statement` (*multipart* `file`) · `financial-statement/text` | P&L / balance-sheet analysis |

## Platform — `/api/platform` (`PlatformController`)

| Area | Endpoints |
|------|-----------|
| Unified score | `POST score` (optionally opens a case) |
| Rules | `GET rules`, `GET rules/history`, `GET rules/{version}`, `POST rules/validate`, `POST rules/publish`, `POST rules/rollback/{version}`, `POST rules/evaluate` |
| Cases | `POST cases`, `GET cases`, `GET cases/stats`, `GET cases/{id}`, `POST cases/{id}/assign`, `POST cases/{id}/status`, `GET/POST cases/{id}/notes`, `POST cases/{id}/decide` (override needs a reason), `GET cases/{id}/audit` |
| Audit | `GET audit`, `GET audit/verify` (walks the SHA-256 chain, reports first tampered sequence) |
| Webhooks | `POST webhooks`, `GET webhooks`, `DELETE webhooks/{id}`, `GET webhooks/deliveries`, `GET webhooks/events`. Events `case.*`, `rules.published`, `model.promoted`, `model.drift_alert`; payloads signed `X-MI-Signature: sha256=HMAC(secret, body)`, retried with back-off |
| Model ops | `GET models`, `GET models/decisions`, `POST models/decisions/{id}/outcome`, `GET models/drift`, `GET models/compare`, `POST models/retrain`, `POST models/promote` |
| MATCH | `POST match/inquiry` → `availability` (`NotConfigured` by default), `found` (`null` = unknown) |

## Conventions

* Failures inside a check are returned as structured *Unavailable* / *Failed* sections with a
  message, not as HTTP 5xx, so callers can still consume the rest of the result.
* Every mutating platform call (publish, rollback, case changes, promote) writes an audit event.
* Streaming uses `application/x-ndjson`; consumers should read line by line.
* Example calls with fixtures live in `src/MerchantIntelligence.Api/MerchantIntelligence.Api.http`
  and in the repo skills under `.agents/skills/`.
