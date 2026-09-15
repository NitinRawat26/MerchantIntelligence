# Web UI

`web/workbench` — Angular 18 + Angular Material single-page app. Dev server on
<http://localhost:4200> proxies `/api` to the .NET API on `:5292` (`proxy.conf.json`); in the Docker
image the API serves the production bundle from `wwwroot` and falls back to `index.html` for any
non-`/api`, non-`/swagger` route.

```bash
cd web/workbench
npm ci
npm start                       # dev server
npm run build                   # production bundle → dist/
CHROME_BIN=<chrome> npx ng test --watch=false --browsers=ChromeHeadless
```

Source layout: `src/app/features/<area>/` (assessment, kyb, mcc, platform, precheck, underwriting),
`src/app/shared/` (field-hint component, common widgets), `app.routes.ts`.

## Sidebar and routes

The sidebar mirrors the run order: *Agentic* → *Pre-check* → *KYB & Screening* → *Financial & Credit*
→ *Decision* → *Review* → *Operations*. Sections collapse by clicking the heading (remembered per
browser). Every form control has an ⓘ hint stating which calculation it feeds.

| Route | Page | API |
|-------|------|-----|
| `/`, `/assess`, `/assess/:id` | **Full assessment** (default): intake form (business, owners, website, MCC, volumes, size & footprint, statement uploads, case options); live run grouped into agent lanes with per-check status and each agent's findings as they stream; decision card; tabbed explainability (identity & screening, website/MCC/business type, financials & plausibility, terms, agents, run log); PDF; history | `/api/assessment/*` |
| `/workflows` | **Workflow editor**: agent cards (mandate, enable, owned checks, stage, waits-for), drag-to-reorder step list with enable toggles, agent assignment, on-failure policy, params, live validation with degraded/unowned warnings, plan + Mermaid preview, version history, load, rollback, publish | `/api/workflows/*` |
| `/precheck` | Website compliance scan and prohibited & restricted business classification (standalone) | `/api/kyb/website-compliance`, `/api/kyb/prohibited-business` |
| `/mcc` | MCC validator | `/api/mcc-validation/*` |
| `/kyb` | Business identity, owners, registry sources, sanctions/PEP/adverse media, website compliance, prohibited verdict | `/api/kyb/*` |
| `/underwriting` | Shapley explainability, reserve & pricing terms, volume plausibility, bank-statement and P&L uploads | `/api/underwriting/*` |
| `/score` | Unified risk score with upstream signals; optional case creation | `/api/platform/score` |
| `/match` | MATCH inquiry (shows `NotConfigured` explicitly) | `/api/platform/match/inquiry` |
| `/cases`, `/cases/:id` | Analyst queue with stats/filters; case detail with assign, status, notes, decide (override reason enforced), audit trail | `/api/platform/cases*` |
| `/rules` | Rule-set JSON editor: validate, publish, evaluate against sample facts, history, rollback | `/api/platform/rules*` |
| `/audit` | Recent audit events; hash-chain verification | `/api/platform/audit*` |
| `/models` | Model registry, champion/challenger, drift, decision log with outcome labelling, retrain/promote | `/api/platform/models*` |
| `/webhooks` | Subscriptions (secret never echoed), deliveries and attempts | `/api/platform/webhooks*` |

## Streaming in `/assess`

The page posts to `POST /api/assessment/run/stream` and reads NDJSON: the first `steps` line seeds
the lanes (steps + agents of the active workflow), `step` lines update check status, `agent` lines
mark an agent Running and later attach its summary/findings, and the final `result` line renders the
decision and explainability tabs. If the stream breaks the UI falls back to polling `GET {id}`.

## Testing the UI

`.agents/skills/suite-ui-testing/SKILL.md` describes the stateful browser scenarios (assessment run,
workflow publish/rollback, case lifecycle) to verify against a locally running API.

## Designed but not built

A drag-and-drop **flow designer** (empty canvas, place the four fixed agents, draw on-success /
on-fail / always transitions, ordered vs. all-parallel lanes, per-step stop-gates, live JSON,
dry-run) was prototyped outside the repo as a standalone HTML mockup. Its extra fields
(`transitions[]`, `agents[].stepOrder`, `steps[].slot`, `steps[].stopGate`) are **not** part of the
current `WorkflowDefinition`; see [Assessment workflow](Assessment-Workflow.md#current-capabilities-vs-designed-extensions).
