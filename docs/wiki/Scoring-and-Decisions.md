# Scoring and decisions

Three deterministic layers turn evidence into an outcome:

1. **Unified risk score** (`Platform/Scoring/UnifiedRiskScorer.cs`) → 0–1000, tier, hard stops, reason codes.
2. **Policy rules** (`Platform/Rules/RulesEngine.cs`, `Resources/default-rules.json`) → Approve / Refer / Decline.
3. **Reserve & pricing terms** (`Underwriting/Pricing/ReservePricingRecommender.cs`) → band A–E and commercial terms (feeds the score as the `Pricing` component).

None of them uses a model beyond the ML.NET credit predictor's probabilities as one input.

## 1. Unified risk score

Weighted average of per-component 0–100 sub-scores, renormalised over the components actually
supplied; uncovered components are reported as coverage gaps.

| Component | Weight | Sub-score source | Reason codes it can raise |
|-----------|--------|------------------|---------------------------|
| `CreditModel` | 0.30 | `P(approve)` from the credit model | `MODEL_DECLINE` (High if P(approve) < 0.2), `MODEL_CANCEL_RISK` |
| `Kyb` | 0.20 | High → 20, Medium → 55, else 90; adjusted for verification and entity age | `KYB_HIGH_RISK`, `BUSINESS_UNVERIFIED` (High), `NEW_ENTITY` |
| `Screening` | 0.15 | sanctions / PEP / adverse-media booleans (`AdverseMedia` is null — uncovered — when every media source failed) | `SANCTIONS_MATCH` (High, **hard stop**), `PEP_MATCH`, `ADVERSE_MEDIA` (Medium; High for ≥ 3 negatives or organised-crime terms), `ADVERSE_MEDIA_MENTION` (Low) |
| `BusinessPolicy` | 0.10 | Prohibited 0 · Restricted 35 · HighRisk 60 · Acceptable 100 | `PROHIBITED_BUSINESS` (High, **hard stop**), `RESTRICTED_BUSINESS`, `HIGH_RISK_BUSINESS` |
| `WebsiteCompliance` | 0.10 | website compliance score | `WEBSITE_NON_COMPLIANT` (< 60) |
| `VolumePlausibility` | 0.10 | plausibility score | `VOLUME_IMPLAUSIBLE` (< 50) |
| `Pricing` | 0.05 | band A 95 · B 80 · C 60 · D 35 · E 15 | `HEAVY_RESERVE_REQUIRED` (D/E) |
| MATCH | – | `matchFound == true` | `MATCH_LISTED` (High, **hard stop**) |

Extra `RiskSignal`s from upstream tools are merged in as reason codes (deduplicated by code).

Algorithm:

```
coverage = Σ weight(covered) / Σ weight(all)
raw      = Σ weighted(covered) / Σ weight(covered)          (50 if nothing covered)
raw     -= (raw − 50) × min(0.5, gaps × 0.03 × 2)            // drift towards the middle per gap
score    = round(clamp(raw, 0, 100) × 10)
if hardStops:                    score = min(score, 150)
elif any High reason:            score = min(score, 549)
tier     = ≥800 VeryLow | ≥650 Low | ≥450 Medium | ≥250 High | VeryHigh
action   = hardStops → Decline
         | score ≥ 650 && coverage ≥ 0.60 && no High reason → Approve
         | otherwise → Refer
```

`POST /api/platform/score` exposes the scorer directly (and can open a case); the `score` step
runs it inside the assessment and then evaluates the rules.

## 2. Policy rules

A **rule set** is versioned JSON in SQLite (`GET/POST /api/platform/rules*`, UI `/rules`).
Rules are evaluated in `priority` order (lower first); every matching rule's `outcome` is collected
and the **most severe wins** (Decline > Refer > Approve); `defaultOutcome` (Refer) applies when
nothing matches.

Condition tree: `{ "fact", "op", "value" }` leaves combined with `all` / `any` / `not`.
Operators: `eq neq gt gte lt lte contains notcontains in exists`.

Facts available (from the unified score + raw application): `score`, `tier`, `coveragePercent`,
`hardStops[]`, `reasonCodes[]`, `highSeverityReasons`, `matchFound`, `annualVolume`,
`averageTicket`, `highestTicket`, `merchantCategoryCode`, `existingRelationship`, plus any field
posted to `rules/evaluate`.

Default set (`default-rules.json`):

| Rule | Priority | Outcome | When |
|------|----------|---------|------|
| `HARD_STOP_SANCTIONS` / `_PROHIBITED` | 1 | Decline | `hardStops contains …` |
| `HARD_STOP_MATCH` | 1 | Decline | `matchFound == true` |
| `LOW_SCORE_DECLINE` | 10 | Decline | `score < 250` |
| `HIGH_SEVERITY_REFER` | 20 | Refer | `highSeverityReasons > 0` |
| `LARGE_VOLUME_REFER` | 20 | Refer | `annualVolume > 5,000,000` |
| `HIGH_TICKET_REFER` | 20 | Refer | `highestTicket > 10,000` |
| `LOW_COVERAGE_REFER` | 25 | Refer | `coveragePercent < 60` |
| `PEP_EDD` | 30 | Refer | `reasonCodes contains PEP_MATCH` |
| `NEW_ENTITY_HIGH_VOLUME` | 30 | Refer | `NEW_ENTITY` and `annualVolume > 1,000,000` |
| `AUTO_APPROVE` | 100 | Approve | `score ≥ 650` and `coveragePercent ≥ 60` and `highSeverityReasons == 0` |

Publishing validates the JSON, stores a new version, activates it and writes an audit event;
rollback creates a new version from an old one. A case decision that contradicts the rules
outcome is an **override** and requires a reason (`POST cases/{id}/decide`).

## 3. Reserve & pricing terms

`ReservePricingRecommender.Recommend(PricingInput)` — surfaced by `POST /api/underwriting/recommend-terms`
and the `terms` step.

**Composite risk (0–1)**

```
risk = P(decline) + 0.5 × P(cancel)                         // credit model
     + MCC tier          medium +0.05 · high +0.15
     + future delivery   ≥ 30 days, up to +0.20
     + card-not-present  share > 0.5 → +0.05 × share
     + subscriptions +0.05 · free trials +0.10
     + KYB high risk +0.20
     + website compliance < 60  → up to +0.15
     + volume plausibility < 60 → up to +0.15
     − existing relationship 0.10
band = <0.15 A | <0.30 B | <0.50 C | <0.70 D | E
```

**Exposure** = `dailyVolume × deliveryDays` + `dailyVolume × 120 × industryChargebackRate × bandMultiplier(1…6)` + `highestTicket`, with delivery days and chargeback rate from `Resources/industry-benchmarks.json` per MCC.

**Reserve**

| Band | Rolling % | Days |
|------|-----------|------|
| A | 0 | – |
| B | 5 | 90 |
| C | 10 | 180 |
| D | 15 | 180 |
| E | 20 | 180 |

MATCH hit forces ≥ 20 % / 180 days. Steady-state balance = daily × % × days, capped at exposure
(type becomes *Capped*). Band E or MATCH adds an **upfront** deposit = min(25 % of exposure,
30 days of volume).

**Pricing**: interchange-plus markup 20 / 35 / 60 / 95 / 150 bps by band (+25 high-risk MCC,
+15 if volume < $100k, −10 if > $10M); per-transaction $0.05–0.20; $15 monthly fee under $250k;
chargeback fee $15/20/25; settlement T+1…T+5; monthly cap = declared monthly × 2.0…1.0;
single-transaction cap = highest ticket × 1.5 (A/B) or × 1.1.

Every adjustment is emitted as a `PricingFactor(code, description, effect)`, shown in the
`/underwriting` page and the PDF. Thresholds are constants in code today.

## Decision derivation in a full assessment

`AssessmentComposer` combines: hard stops → **Decline**; step `onFail: Refer` failures or rules
outcome Refer → **Refer**; otherwise the rules outcome. The result names the deciding rule, the tier,
coverage %, reason codes, Shapley contributions of the credit model, agent findings, next steps and
source limitations. See [../full-assessment.md §7–9](../full-assessment.md#7-decision-derivation).
