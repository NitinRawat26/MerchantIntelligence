# Assessment workflow engine

Code: `src/MerchantIntelligence.Platform/Workflows/` — `WorkflowModels.cs`, `WorkflowPlanner.cs`,
`WorkflowRunner.cs`, `WorkflowRepository.cs`, `AssessmentContext.cs`, `IAssessmentAgent.cs`.
Entry point: `AssessmentService.RunAsync` (`Platform/Assessment/`). API: `/api/workflows`, `/api/assessment`.

## Contracts

```csharp
interface IAssessmentStep  { WorkflowStepDescriptor  Descriptor; Task ExecuteAsync(AssessmentContext ctx); }
interface IAssessmentAgent { WorkflowAgentDescriptor Descriptor; Task<AgentReview> ReviewAsync(AssessmentContext ctx, IReadOnlyList<string> ownedSteps); }

record WorkflowStepDescriptor(Id, Name, Description, DependsOn[], Consumes[], Required, Params[]);
record WorkflowAgentDescriptor(Id, Name, Mandate, Description, DefaultSteps[]);
```

* A **step** is one check (e.g. `screening`). It reads intake and earlier results from
  `AssessmentContext`, calls the domain service, and records a typed result plus a `StepStatus`
  (Pending / Running / Succeeded / Failed / Skipped). Steps can skip themselves (`ctx.SkipAsync`)
  when their input is absent — e.g. `website` when no URL was given.
* An **agent** owns steps and adds a deterministic `ReviewAsync` after they finish. Both are
  registered in DI (`Platform/Agents/<Agent>/<Agent>AgentRegistration.cs`); the planner discovers
  them through `IEnumerable<IAssessmentStep>` / `IEnumerable<IAssessmentAgent>`.
* `AssessmentContext` is the shared state: intake, per-step results, findings, `HardStop`, agent
  reports, and a progress callback used for streaming.

## Workflow definition (data)

```json
{
  "name": "Standard onboarding",
  "version": "default",
  "haltOnHardStop": false,
  "agents": [
    { "id": "precheck",  "enabled": true, "steps": ["website", "prohibited", "mcc"] },
    { "id": "kyb",       "enabled": true, "steps": ["verification", "screening", "match", "presence"] },
    { "id": "financial", "enabled": true, "steps": ["bank", "financials", "plausibility", "credit"] },
    { "id": "decision",  "enabled": true, "steps": ["terms", "score", "case"] }
  ],
  "steps": [
    { "id": "screening", "enabled": true, "onFail": "Skip",
      "params": { "includeOwners": true }, "dependsOn": null },
    ...
  ]
}
```

| Field | Meaning |
|-------|---------|
| `steps[].enabled` | Disabled → recorded as *Skipped* coverage gap; dependants are flagged *degraded* |
| `steps[].onFail` | `Skip` (coverage gap, default) · `Refer` (continue, force final Refer) · `Abort` (run fails) |
| `steps[].dependsOn` | `null` keeps the catalogue defaults; a list overrides them |
| `steps[].params` | Validated against the step's `Params` descriptors |
| `agents[].steps` | Ownership; every step must be owned by exactly one agent |
| `agents[].enabled` | Disabled agent → all its steps skipped |
| `haltOnHardStop` | Once `ctx.HardStop` is set (sanctions / prohibited / MATCH), remaining non-required steps are skipped |

The embedded default is `Platform/Resources/default-workflow.json`. Stored versions are
`Upgrade`d on read so older definitions gain new steps/agents (disabled-by-default rules apply).

## Planner

`WorkflowPlanner.Plan(def)` validates and produces `WorkflowPlan(Stages, Warnings, Disabled, Mermaid, Agents)`:

1. **Validation** (throws `WorkflowValidationException`): name present, ≥1 step, unknown/duplicate
   step or agent ids, self- or unknown dependencies, unknown params, every catalogue step and agent
   listed, a dependency ordered after its dependant, step owned by two agents or by none, agent
   dependency cycles. Missing **required** steps (`score`) fail validation.
2. **Step stages**: walk `steps[]` in list order; a step joins the earliest stage where all its
   active dependencies are satisfied. Steps in one stage run concurrently.
3. **Agent stages**: an agent depends on another when one of its steps depends on a step the other
   owns. Agents with no cross-dependency share a stage (Pre-check ∥ KYB → Financial → Decision).
4. **Warnings**: degraded steps (dependency disabled), unowned/ disabled agents, etc. Returned to
   the editor but do not block publish.

## Runner

`WorkflowRunner.RunAsync(def, ctx)` compiles the plan into a Microsoft Agent Framework graph:

```
start ──fan-out──▶ [AgentExecutor per enabled agent in stage 1] ──▶ StageGate(1)
      ──fan-out──▶ [stage 2 agents]                             ──▶ StageGate(2)
      ...                                                        ──▶ finish (output)
```

* `AgentExecutor` (`Executor<RunToken, RunToken>`, id `agent-<id>`) emits `AgentProgressEvent`
  (Running), runs its owned steps stage-by-stage with `Task.WhenAll`, then calls
  `agent.ReviewAsync` and emits the final `AgentReport` (status, summary, findings, elapsed ms).
  A review exception is recorded as a failed review but does **not** fail the run.
* `StageGate` forwards one token once every executor of the previous stage has reported.
* Step status changes flow out as `StepProgressEvent` via `ctx.Capture(...)`; the API turns these
  into NDJSON lines for `POST /api/assessment/run/stream`.
* `HaltOnHardStop` is enforced in `AgentExecutor.RunStep`: non-required steps other than `case`
  are skipped once `ctx.HardStop` is set.

After the graph completes, `AssessmentComposer` derives the decision, explainability report and
persists the `AssessmentResult` (including `agents[]`), and `AssessmentPdfRenderer` produces the memo.

## Versioning and audit

`WorkflowRepository` stores each publish as a new version row (`author`, `comment`, `createdAt`,
`active`). `POST /api/workflows/publish` validates → stores → activates and writes an audit event;
`POST rollback/{version}` creates a new version copying the old definition. The Angular
`/workflows` page is a front-end over these endpoints (drag-to-reorder steps, agent assignment,
toggles, params, live validation, plan preview with Mermaid graph, history).

## Current capabilities vs. designed extensions

Supported today: fixed set of four agents, step ownership, enable/disable, list order, `dependsOn`
overrides, per-step params, `onFail` policy, global `haltOnHardStop`, versioning/rollback.

Discussed but **not implemented** (design notes only): explicit agent-to-agent transitions
(on success / on fail / always), per-agent ordered-vs-parallel switch, per-step stop-gate rules,
nested sub-agents (`agents[].children[]`), dynamic top-level agent creation, and configurable review
rules. The planner/runner shape (plan → stages → graph with gates) is the intended extension point;
`dependsOn` remains a hard data constraint that any future ordering UI must respect.
