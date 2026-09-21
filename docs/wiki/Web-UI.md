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
| `/`, `/assess`, `/assess/:id` | **Full assessment** (default): intake form (business, owners, website, MCC, volumes, size & footprint, statement uploads, case options); live run grouped into agent lanes with per-check status and each agent's findings as they stream; decision card; tabbed explainability (identity & screening incl. adverse-media evidence list with quoted context and matched risk terms, website/MCC/business type, financials & plausibility, terms, agents, run log); PDF; history | `/api/assessment/*` |
| `/workflows` | **Workflow designer** (see below): agent-flow canvas with Start/End, drag the five fixed agents in (Profile is pinned first and locked), draw on-success / on-fail / always transitions; step lanes per agent (Ordered with slots, or All parallel) with drag-and-drop between lanes, dependency badges, stop-gate toggle; inspector; full-screen mode; JSON tab; dry-run plan (stages, Mermaid); version history, load, rollback, publish | `/api/workflows/*` |
| `/precheck` | Website compliance scan and prohibited & restricted business classification (standalone) | `/api/kyb/website-compliance`, `/api/kyb/prohibited-business` |
| `/mcc` | MCC validator | `/api/mcc-validation/*` |
| `/kyb` | Business identity, owners, registry sources, sanctions/PEP/adverse media (per-source status pills, article tone, matched risk terms, category and quoted context), website compliance, prohibited verdict | `/api/kyb/*` |
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

## Workflow designer (`/workflows`)

Code: `features/platform/workflows.component.ts` (page: toolbar, tabs, publish/history) and
`workflow-designer.component.ts` (the designer itself, Angular CDK drag-and-drop, no canvas library).

* **Default first, blank on demand** – *Default flow* loads the embedded definition, *Reload active*
  the published one, *New workflow* an empty canvas with every step in the *checks not in this
  workflow* palette. The agent palette is fixed at the five agents; there is no agent creation. The **Profile agent**
  is governed: no incoming port or transition, no drag, cannot be disabled or removed, its `entity` /
  `segment` steps cannot leave its lane, be gated or be turned off, and evidence steps cannot be
  dropped into its lane — each refusal shows the reason as a notice. The server-side planner enforces
  the same rules on publish.
* **Agent flow canvas** – drop agents anywhere; each has a distinct colour (Pre-check purple, KYB
  blue, Financial green, Decision orange). Drag from an agent's right port to another agent to add a
  transition; click its label to cycle **always → on success → on fail**; select it to remove.
  **Start** wires to agents with no incoming transition (they begin at once, in parallel), **End**
  from agents with no outgoing one. When a workflow has no explicit transitions the planner's
  dependency-inferred order is drawn as dashed wires. An arrow that would loop the flow (through
  transitions or cross-agent `dependsOn`) is refused with a notice naming the existing route.
* **Step lanes** – one lane per placed agent. **Ordered** lanes number each card with a slot (equal
  slots run together); **All parallel** lanes dispatch everything at once. Steps dispatched together
  are bracketed and labelled *∥ run together*. `↳ dep` badges are the step's data dependencies; a drop
  that would put a step above one it needs (or below one that needs it) is re-sorted and explained –
  `dependsOn` always wins over the chosen order.
* **Inspector** – agent: enable, Ordered/All parallel, stage, waits-for; transition: condition,
  remove; step: enable, on-fail policy, params, and the **stop-gate** (trigger, flag code, scope
  agent/workflow, forced Refer/Decline).
* **Full screen** – uses the browser Fullscreen API so the designer covers the whole window
  including the sidebar; Esc or the button returns.
* **JSON / Dry run / Version history** tabs – the live definition (editable), the validated plan
  (agent stages, per-agent step stages, warnings, Mermaid) and stored versions with rollback.

Field semantics are in [Assessment workflow](Assessment-Workflow.md#workflow-definition-data).
Not implemented: nested sub-agents (`agents[].children[]`), dynamic top-level agents, the mockup's
`else` fallback route and `skipRemaining` / `cancelParallel` stop-gate effects.
