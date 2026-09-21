# Assessment workflow engine

Code: `src/MerchantIntelligence.Platform/Workflows/` — `WorkflowModels.cs`, `WorkflowPlanner.cs`,
`WorkflowRunner.cs`, `StopGates.cs`, `WorkflowRepository.cs`, `AssessmentContext.cs`, `IAssessmentAgent.cs`.
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
    { "id": "precheck",  "enabled": true, "stepOrder": "Parallel", "steps": ["website", "prohibited", "mcc"] },
    { "id": "kyb",       "enabled": true, "stepOrder": "Parallel", "steps": ["verification", "screening", "match", "presence"] },
    { "id": "financial", "enabled": true, "stepOrder": "Ordered",  "steps": ["bank", "financials", "plausibility", "credit"] },
    { "id": "decision",  "enabled": true, "stepOrder": "Parallel", "steps": ["terms", "score", "case"] }
  ],
  "transitions": [
    { "from": "precheck",  "to": "decision",  "when": "Always" },
    { "from": "kyb",       "to": "financial", "when": "Always" },
    { "from": "kyb",       "to": "decision",  "when": "Always" },
    { "from": "financial", "to": "decision",  "when": "Always" }
  ],
  "steps": [
    { "id": "screening", "enabled": true, "onFail": "Skip",
      "params": { "includeOwners": true }, "dependsOn": null,
      "stopGate": { "when": "HardStop", "scope": "Workflow", "forceOutcome": "Decline" } },
    { "id": "bank",       "enabled": true, "onFail": "Skip", "slot": 1 },
    { "id": "financials", "enabled": true, "onFail": "Skip", "slot": 1 },
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
| `agents[].enabled` | Disabled agent → all its steps skipped. The `Profiling` agent (`profile`) cannot be disabled |
| `agents[].stepOrder` | `Parallel` (default): the agent's steps are dispatched together and only `dependsOn` sequences them · `Ordered`: steps run in `slot` order |
| `steps[].slot` | Position inside an `Ordered` agent; equal slots run together; omitted → list position. Ignored when the agent is `Parallel` |
| `steps[].stopGate` | Rule evaluated right after the step: `when` = `HardStop` · `Failed` · `HighSeverityFlag` · `Flag` (+ `code`); `scope` = `Agent` (skip the agent's remaining non-required steps) · `Workflow` (skip them in every agent still to run); `forceOutcome` = `None` · `Refer` · `Decline` (Approve can never be forced). A fired gate marks the owning agent **Failed**. Not allowed on `score` / `case` |
| `transitions[]` | Agent-to-agent control flow: `{from, to, when}` with `when` = `Always` · `Success` · `Fail`. The target waits for the source to settle and runs when the condition holds; with several incoming transitions it runs when at least one holds, otherwise it is **Skipped**. Agents with no incoming transition (and no cross-agent dependency) start immediately, in parallel. One transition per pair, no self-loops, no cycles |
| `haltOnHardStop` | Legacy switch kept for old definitions: once `ctx.HardStop` is set, remaining non-required steps are skipped. Stop-gates are the configurable replacement |

**Three kinds of ordering, in priority order.** `dependsOn` is a hard data constraint (plausibility
needs bank + financials) and always wins; `slot` / `stepOrder` is a scheduling preference inside an
agent; `transitions` are control flow between agents. When a chosen order contradicts a dependency
the planner keeps the dependency and emits a warning ("'plausibility' is slotted before 'bank' … but
needs their output – it runs after them").

The embedded default is `Platform/Resources/default-workflow.json` (all agents `Parallel`, no
stop-gates, the profile agent first, the `Always` transitions above). Stored versions are `Upgrade`d on read so older
definitions gain new steps/agents (disabled-by-default rules apply) and the new fields' defaults;
a legacy definition without `transitions` keeps its dependency-inferred agent order.

## Profile-first governance

The catalogue carries one agent of kind `Profiling` (`profile`, steps `entity` + `segment`, both
marked `Profiling` in their descriptor). It classifies the applicant from intake alone and publishes
the `MerchantProfile` every other agent reads, so the planner treats it differently from the four
evidence agents:

* exactly one profiling agent must be present and enabled — a workflow without it, or with it
  disabled, fails validation ("… profiles the applicant and decides which checks apply; it must run
  first and cannot be disabled");
* it owns every `Profiling` step and no evidence step; profile steps may not depend on evidence
  steps and may not carry a stop-gate (they scope the run, they do not decide it);
* no transition may point **into** it ("Transition 'kyb' → 'profile' is not allowed: the profiling
  agent always runs first, nothing can run before it");
* every evidence step receives an implicit dependency on the profile steps, so the profile agent
  always forms stage 1 on its own and every other agent `WaitsFor` it, whatever the transitions say;
* `WorkflowPlan.ProfileAgentId` / `ProfileSteps` expose the result to the runner and the UI.

At run time the runner consults `ctx.Profile.NotApplicable` before starting any evidence step and
skips the step with an audit line ("Not applicable to a Small multi-member LLC: …"). The designer
mirrors the rules client-side: the profile node is pinned (no drag, no incoming port, no remove, no
disable), its steps cannot be dragged to other lanes or gated, and evidence steps cannot be dropped
into its lane. See [steps/profile.md](../steps/profile.md).

## Planner

`WorkflowPlanner.Plan(def)` validates and produces `WorkflowPlan(Stages, Warnings, Disabled, Mermaid, Agents)`:

1. **Validation** (throws `WorkflowValidationException`): name present, ≥1 step, unknown/duplicate
   step or agent ids, self- or unknown dependencies, unknown params, every catalogue step and agent
   listed, step owned by two agents or by none, negative slots, a `Flag` stop-gate without a code, a
   stop-gate on a deciding or profiling step, a missing/disabled/second profiling agent, a profile step
   owned elsewhere or depending on evidence, a transition into the profiling agent, transitions to/from unknown agents, self-transitions, duplicate
   transitions, dependency cycles between steps, and cycles between agents *through transitions or
   step dependencies* ("Make the flow run one way only"). Missing **required** steps (`score`) fail
   validation. List position of a step no longer implies order.
2. **Step stages per agent**: `Ordered` → group by `slot`; `Parallel` → one group; then each step is
   pushed to the earliest group after all its active dependencies (dependency wins, warning emitted).
   Steps in one group run concurrently (`WorkflowAgentPlan.StepStages`).
3. **Agent stages**: from `transitions` (a target sits after every source) merged with cross-agent
   `dependsOn`, after the profiling agent, which always forms the first stage alone (default: Profile → Pre-check ∥ KYB ∥ Financial → Decision).
   `WorkflowAgentPlan.RunsWhen` lists the incoming transitions, `WaitsFor` the agents it waits on.
4. **Warnings**: degraded steps (dependency disabled), disabled agents, dependency-forced slots,
   conditional agents ("runs only when 'kyb' fails; otherwise it is skipped"), agents behind a
   disabled source, what each stop-gate will do, etc. Returned to the editor but do not block
   publish.

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
* **Outcomes and transitions**: each agent records an `AgentOutcome` (`Succeeded` · `Failed` ·
  `Skipped`) on the context. Before an agent runs, its incoming transitions are matched against the
  sources' outcomes — `Always`, `Success` (no failed step, no fired gate) or `Fail`; if none holds the
  agent is `Skipped` and reported like a disabled one (its steps become coverage gaps). A step whose
  `onFail` is `Abort` fails the run as before.
* **Stop-gates** (`StopGates.cs`): evaluated deterministically right after the step completes from
  the step's own recorded result (hard stop, failure, high-severity flag or a named flag code). When
  it fires the remaining non-required evidence steps in scope are skipped, the owning agent ends
  `Failed` (so `Fail` transitions take effect), the hit is recorded in the agent report and the
  forced `Refer` / `Decline` is carried to the composer, which can only tighten the decision.
* Step status changes flow out as `StepProgressEvent` via `ctx.Capture(...)`; the API turns these
  into NDJSON lines for `POST /api/assessment/run/stream` (a skipped agent arrives as an `agent`
  line with status `Skipped` and the reason).
* `HaltOnHardStop` is still honoured in `AgentExecutor.RunStep` for legacy definitions: non-required
  steps other than `case` are skipped once `ctx.HardStop` is set.

After the graph completes, `AssessmentComposer` derives the decision, explainability report and
persists the `AssessmentResult` (including `agents[]`), and `AssessmentPdfRenderer` produces the memo.

## Versioning and audit

`WorkflowRepository` stores each publish as a new version row (`author`, `comment`, `createdAt`,
`active`). `POST /api/workflows/publish` validates → stores → activates and writes an audit event;
`POST rollback/{version}` creates a new version copying the old definition. The Angular
`/workflows` page is the visual designer over these endpoints — see
[Web UI → Workflow designer](Web-UI.md#workflow-designer-workflows).

## Current capabilities vs. designed extensions

Supported today: fixed set of five agents (profile pinned first), step ownership, enable/disable, `dependsOn` overrides,
per-step params, `onFail` policy, per-agent `Ordered` / `Parallel` with `slot`s, per-step
stop-gates (trigger, scope, forced outcome), agent transitions (`Always` / `Success` / `Fail`) with
skipped-agent reporting, legacy `haltOnHardStop`, versioning/rollback.

Discussed but **not implemented** (design notes only): nested sub-agents (`agents[].children[]` —
a child is an agent definition parented to one of the evidence agents, compiled as a sub-workflow by its
parent executor), dynamic top-level agent creation, an `else` fallback route on transitions,
`skipRemaining` / `cancelParallel` stop-gate effects, and configurable review rules. `dependsOn`
remains a hard data constraint that any ordering feature must respect.
