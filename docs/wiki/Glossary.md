# Glossary

| Term | Meaning in this repo |
|------|----------------------|
| **Acquirer** | The bank/processor that boards a merchant and settles its card transactions; the suite's user |
| **Agent** | One of four fixed deterministic executors (Pre-check, KYB & screening, Financial & credit, Decision & case) that owns steps and adds a coded review. Not an LLM agent |
| **Agent Framework** | `Microsoft.Agents.AI.Workflows` — the graph runtime (executors, edges, fan-out/fan-in, events) used to run agents concurrently. Successor of AutoGen + Semantic Kernel; used here without any model |
| **Assessment** | One run of the active workflow over one intake; persisted with id `ASMT-…` |
| **Band (A–E)** | Pricing/reserve risk band from the composite risk in `ReservePricingRecommender` |
| **Beneficial owner / principal** | Individuals behind the business, screened against sanctions/PEP lists |
| **Coverage** | Share of score weight backed by checks that actually ran; gaps are named and lower certainty |
| **Coverage gap** | A check that was skipped, failed, or whose source was unavailable |
| **`dependsOn`** | Hard data dependency between steps; the planner never schedules a step before its dependencies |
| **EDGAR** | SEC's filing system; used live for registry verification and offline for the MCC filer index / classifier training |
| **Evidence provider** | One voter in MCC validation (keywords, ML classifier, schema.org, EDGAR SIC) |
| **Finding** | Structured output of an agent review: code, severity, message, suggested action |
| **Flag / reason code** | SCREAMING_SNAKE code with severity emitted by a check (e.g. `NEW_ENTITY`, `MCC_MISMATCH`) |
| **GLEIF / LEI** | Global Legal Entity Identifier registry |
| **Hard stop** | `SANCTIONS_MATCH`, `PROHIBITED_BUSINESS`, `MATCH_LISTED` — caps the score at 150 and decides Decline |
| **Intake** | The single form/JSON collected once for an assessment (`AssessmentIntake`) |
| **KYB** | Know Your Business — identity, registry, screening and compliance checks |
| **Local presence** | Geocoded POI lookup (OSM/Foursquare/Google) proving a small merchant trades at its address |
| **MATCH / TMF** | Mastercard's terminated-merchant file; requires acquirer credentials, otherwise `NotConfigured` |
| **MCC** | Merchant Category Code (ISO 18245), e.g. 5812 restaurants |
| **Override** | Analyst case decision that contradicts the rules outcome; requires a reason and is audited |
| **PEP** | Politically exposed person; triggers enhanced due diligence (`PEP_EDD` rule) |
| **Plan / stage** | Planner output: sets of steps (and agents) that run concurrently, in order |
| **Plausibility** | Whether declared volumes/tickets/headcount are believable given benchmarks and statements |
| **Prohibited / Restricted / High-risk** | Business policy classes from `restricted-categories.json` |
| **Reserve** | Funds withheld from settlements (rolling %, days; capped; upfront) to cover exposure |
| **Review** | Deterministic `ReviewAsync` an agent runs after its steps; emits findings only |
| **Rule set** | Versioned JSON policy evaluated on score facts to produce Approve/Refer/Decline |
| **Shapley value** | Exact per-feature attribution of the credit model's probability vs. a baseline merchant |
| **SIC** | SEC industry code; mapped to MCC via a curated crosswalk (weak labels) |
| **Step** | One check implementing `IAssessmentStep`; 14 exist |
| **Stop-gate** | Per-step rule (`steps[].stopGate`) evaluated right after the step: on hard stop / failure / high-severity flag / named flag it skips the remaining evidence steps of the agent or the whole workflow, marks the agent Failed and can force Refer or Decline |
| **Transition** | Agent-to-agent control flow (`transitions[]`): the target runs after the source when `Always` / `Success` / `Fail` holds; an agent with no holding transition is Skipped |
| **Slot** | Position of a step inside an `Ordered` agent; equal slots run together. `dependsOn` overrides slots |
| **Unified risk score** | 0–1000 weighted composite over covered components; tier VeryLow…VeryHigh |
| **Unavailable** | Result status when a source could not be reached; excluded from the score, never read as clear |
| **Workflow definition** | Versioned JSON describing agents (ownership, `stepOrder`), steps (params, on-fail, `slot`, `stopGate`) and `transitions` |
