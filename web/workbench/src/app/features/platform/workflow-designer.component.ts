import { Component, ElementRef, computed, effect, input, output, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CdkDragDrop, CdkDragEnd, DragDropModule, moveItemInArray, transferArrayItem } from '@angular/cdk/drag-drop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  AgentStepOrder, ForcedOutcome, StepFailurePolicy, StopGateScope, StopGateTrigger, TransitionCondition, WorkflowAgentConfig, WorkflowAgentDescriptor,
  WorkflowDefinition, WorkflowPlan, WorkflowStepConfig, WorkflowStepDescriptor, WorkflowTransition
} from '../../shared/models';

export const AGENT_ICONS: Record<string, string> = { precheck: 'fact_check', kyb: 'verified_user', financial: 'account_balance', decision: 'gavel' };
/** One colour per fixed agent, used on canvas nodes, lanes, palette and inspector so an agent is recognisable everywhere. */
export const AGENT_COLORS: Record<string, string> = { precheck: '#7b3fa0', kyb: '#0b6fa4', financial: '#2e7d32', decision: '#d84315' };

export const POLICIES: { value: StepFailurePolicy; label: string; hint: string }[] = [
  { value: 'Skip', label: 'Skip (coverage gap)', hint: 'Failed check becomes a coverage gap.' },
  { value: 'Refer', label: 'Refer (no Approve)', hint: 'Coverage gap, and the decision cannot be Approve.' },
  { value: 'Abort', label: 'Abort assessment', hint: 'The whole assessment fails.' }
];

const CONDITIONS: { value: TransitionCondition; label: string; hint: string }[] = [
  { value: 'Always', label: 'always', hint: 'Runs once the source agent has finished, whatever its outcome.' },
  { value: 'Success', label: 'on success', hint: 'Runs only when the source agent finished without a failed step, stop-gate or High finding.' },
  { value: 'Fail', label: 'on fail', hint: 'Runs only when the source agent failed – a fallback route.' }
];

const TRIGGERS: { value: StopGateTrigger; label: string }[] = [
  { value: 'HardStop', label: 'hard stop confirmed (sanctions / prohibited / MATCH)' },
  { value: 'Failed', label: 'the step fails' },
  { value: 'HighSeverityFlag', label: 'a High or Very high flag is raised' },
  { value: 'Flag', label: 'a specific flag code is raised' }
];

const SCOPES: { value: StopGateScope; label: string }[] = [
  { value: 'Agent', label: 'skip the rest of this agent' },
  { value: 'Workflow', label: 'skip remaining evidence steps everywhere' }
];

const OUTCOMES: { value: ForcedOutcome; label: string }[] = [
  { value: 'None', label: 'leave the decision to score & rules' },
  { value: 'Refer', label: 'force Refer' },
  { value: 'Decline', label: 'force Decline' }
];

const NODE_W = 172;
const NODE_H = 64;
const STAGE_GAP = 38;
const TERM_R = 22;
const TERM_GAP = 44;

type Selection = { kind: 'agent'; id: string } | { kind: 'transition'; index: number } | { kind: 'step'; id: string } | null;

interface Point { x: number; y: number; }

/**
 * Visual editor for a workflow definition: the four fixed agents on a canvas connected by conditional transitions,
 * one step lane per placed agent (ordered or all-parallel), and an inspector for the selected agent, transition or step.
 * Mutates the bound definition in place and emits `changed` so the host can re-validate.
 */
@Component({
  selector: 'mi-workflow-designer',
  standalone: true,
  imports: [FormsModule, DragDropModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule, MatTooltipModule],
  styleUrl: './platform.scss',
  template: `
    @if (def(); as d) {
      <div class="designer" (mousemove)="trackLink($event)" (mouseup)="cancelLink()">
        <div class="left">
          <h3><mat-icon inline>hub</mat-icon> Agent flow <span class="muted">drag agents from the palette · drag from a port to connect · click an arrow to set the condition</span></h3>
          <div class="flow">
            <div class="palette" cdkDropList id="agent-palette" [cdkDropListData]="unplacedAgentIds()" [cdkDropListConnectedTo]="['agent-canvas']" cdkDropListSortingDisabled>
              <div class="pal-title">Agents</div>
              @for (a of unplacedAgents(); track a.id) {
                <div class="pal-item agent-item" cdkDrag [cdkDragData]="a.id" [matTooltip]="a.mandate" [style.--agent]="color(a.id)">
                  <mat-icon>{{ icon(a.id) }}</mat-icon><span>{{ a.name }}</span>
                  <div *cdkDragPlaceholder class="pal-item placeholder"></div>
                </div>
              }
              @if (!unplacedAgents().length) { <div class="muted small">All four agents are on the canvas.</div> }
            </div>

            <div #canvas class="canvas" (click)="select(null)">
              <div class="drop-target" cdkDropList id="agent-canvas" [cdkDropListData]="placedIds()" cdkDropListSortingDisabled (cdkDropListDropped)="dropAgent($event)"></div>
              <svg class="wires" [attr.width]="canvasW()" [attr.height]="canvasH()">
                <defs>
                  <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="currentColor"/></marker>
                </defs>
                @if (placed().length) {
                  @for (w of terminalWires(); track $index) { <path [attr.d]="w" class="term-wire" marker-end="url(#arrow)"/> }
                  @for (w of inferredWires(); track $index) { <path [attr.d]="w" class="wire inferred" marker-end="url(#arrow)" matTooltip="Order inferred from data dependencies – add a transition to make it explicit"/> }
                  <g class="terminal" [attr.transform]="'translate(' + startPos().x + ',' + startPos().y + ')'">
                    <circle [attr.r]="termR" class="start"/><text y="4" text-anchor="middle">Start</text>
                  </g>
                  <g class="terminal" [attr.transform]="'translate(' + endPos().x + ',' + endPos().y + ')'">
                    <circle [attr.r]="termR" class="end"/><circle [attr.r]="termR - 5" class="end-inner"/><text y="4" text-anchor="middle">End</text>
                  </g>
                }
                @for (t of d.transitions ?? []; track $index; let i = $index) {
                  @if (wire(t); as w) {
                    <g class="wire" [class.selected]="isSelected('transition', i)" [class.fail]="t.when === 'Fail'" [class.success]="t.when === 'Success'" (click)="pick({ kind: 'transition', index: i }, $event)">
                      <path [attr.d]="w.d" class="hit"/>
                      <path [attr.d]="w.d" class="line" marker-end="url(#arrow)"/>
                      <rect [attr.x]="w.mx - 34" [attr.y]="w.my - 10" width="68" height="20" rx="10" class="label-bg" (click)="cycle(i, $event)"/>
                      <text [attr.x]="w.mx" [attr.y]="w.my + 4" text-anchor="middle" class="label" (click)="cycle(i, $event)">{{ conditionLabel(t.when) }}</text>
                    </g>
                  }
                }
                @if (linking(); as l) {
                  <path class="wire pending" [attr.d]="pendingWire(l)" marker-end="url(#arrow)"/>
                }
              </svg>

              @if (!placed().length) {
                <div class="canvas-empty"><mat-icon>add_circle_outline</mat-icon> Drop agents here to start the flow. Agents with no incoming arrow start immediately; agents side by side run in parallel.</div>
              }

              @for (a of placed(); track a.id) {
                <div class="node" cdkDrag [cdkDragData]="a.id" [cdkDragFreeDragPosition]="posOf(a.id)" cdkDragBoundary=".canvas"
                     (cdkDragEnded)="nodeMoved(a.id, $event)" [class.selected]="isSelected('agent', a.id)" [class.disabled]="!a.enabled"
                     [class.link-target]="linking() && linking()!.from !== a.id" (click)="pick({ kind: 'agent', id: a.id }, $event)"
                     (mouseup)="finishLink(a.id, $event)" [style.--agent]="color(a.id)">
                  <div class="port in" matTooltip="incoming"></div>
                  <div class="node-body">
                    <mat-icon>{{ icon(a.id) }}</mat-icon>
                    <div class="node-text">
                      <strong>{{ name(a.id) }}</strong>
                      <span class="muted small">{{ agentSummary(a) }}</span>
                    </div>
                  </div>
                  <div class="port out" matTooltip="drag to the next agent" (mousedown)="startLink(a.id, $event)"></div>
                </div>
              }
            </div>
          </div>

          <h3><mat-icon inline>view_column</mat-icon> Steps per agent <span class="muted">drag steps from the palette into a lane · drag between lanes to reassign · Ordered lanes run top-down, All parallel lanes let dependencies decide</span></h3>
          <div class="lanes-wrap">
            <div class="palette steps-palette" cdkDropList id="step-palette" [cdkDropListData]="unplacedSteps" [cdkDropListConnectedTo]="laneIds()" (cdkDropListDropped)="dropStep($event)">
              <div class="pal-title">Checks not in this workflow</div>
              @for (id of unplacedSteps; track id) {
                <div class="pal-item step-item" cdkDrag [cdkDragData]="id" [class.required-item]="describe(id)?.required" [matTooltip]="describe(id)?.description ?? ''">
                  <span>{{ describe(id)?.name ?? id }}</span>
                  @if (describe(id)?.required) { <mat-icon inline matTooltip="Required for a decision">star</mat-icon> }
                </div>
              }
              @if (!unplacedSteps.length) { <div class="muted small">Every check is placed.</div> }
            </div>

            <div class="lanes">
              @if (notice(); as n) { <div class="notice wide"><mat-icon inline>swap_vert</mat-icon> {{ n }}</div> }
              @for (a of placed(); track a.id) {
                <div class="lane" [class.disabled]="!a.enabled" [class.selected]="isSelected('agent', a.id)" [style.--agent]="color(a.id)">
                  <div class="lane-head" (click)="pick({ kind: 'agent', id: a.id }, $event)">
                    <mat-icon>{{ icon(a.id) }}</mat-icon>
                    <strong>{{ name(a.id) }}</strong>
                    @if (agentPlan(a.id); as ap) {
                      @if (ap.enabled) { <span class="pill small good">stage {{ ap.stage }}</span> } @else { <span class="pill small warn">skipped</span> }
                    }
                    <span class="spacer"></span>
                    <mat-button-toggle-group class="order-toggle" hideSingleSelectionIndicator [value]="a.stepOrder ?? 'Parallel'" (change)="setOrder(a, $event.value)" (click)="$event.stopPropagation()">
                      <mat-button-toggle value="Ordered" matTooltip="Run top-down; equal slot numbers run together">Ordered</mat-button-toggle>
                      <mat-button-toggle value="Parallel" matTooltip="Run everything at once, except where a step needs another's output">All parallel</mat-button-toggle>
                    </mat-button-toggle-group>
                  </div>
                  <div class="lane-body" cdkDropList [id]="laneId(a.id)" [cdkDropListData]="a.steps" [cdkDropListConnectedTo]="laneIds(a.id)" (cdkDropListDropped)="dropStep($event)">
                    @for (id of a.steps; track id; let i = $index) {
                      @if (step(id); as s) {
                        <div class="step-card" cdkDrag [cdkDragData]="id" [class.selected]="isSelected('step', id)" [class.off]="!s.enabled" [class.required]="describe(id)?.required"
                             [class.grp]="groupPos(a, i)" [class.grp-start]="groupPos(a, i) === 'start'" [class.grp-end]="groupPos(a, i) === 'end'"
                             (click)="pick({ kind: 'step', id }, $event)">
                          @if (groupPos(a, i) === 'start') { <div class="grp-label" matTooltip="These steps are dispatched at the same time (a dependency badge still forces a step to wait for its input)">∥ run together</div> }
                          <div class="slot" [class.hidden]="(a.stepOrder ?? 'Parallel') !== 'Ordered'" matTooltip="Slot – steps with the same number run together">{{ slotOf(a, i) }}</div>
                          <div class="step-main">
                            <div class="step-title"><strong>{{ describe(id)?.name ?? id }}</strong>
                              @if (describe(id)?.required) { <mat-icon inline matTooltip="Decision authority: disabling forces Refer">star</mat-icon> }
                              @if (!s.enabled) { <span class="pill small warn">off</span> }
                              @if (s.stopGate) { <span class="pill small bad" matTooltip="Stop-gate configured"><mat-icon inline>block</mat-icon> gate</span> }
                              @if (s.onFail !== 'Skip') { <span class="pill small neutral">{{ s.onFail }} on fail</span> }
                            </div>
                            <div class="badges">
                              @for (dep of deps(s); track dep) {
                                <span class="pill small" [class.good]="isOn(dep)" [class.bad]="!isOn(dep)" matTooltip="Needs the output of this step – it always runs after it">↳ {{ dep }}</span>
                              }
                              @if (stepStage(a.id, id); as st) { <span class="muted small">runs {{ st }}</span> }
                            </div>
                            @for (w of stepWarnings(id); track w) { <div class="warn-line"><mat-icon inline>warning</mat-icon> {{ w }}</div> }
                          </div>
                          <div *cdkDragPlaceholder class="step-card placeholder"></div>
                        </div>
                      }
                    }
                    @if (!a.steps.length) { <div class="lane-empty">Drop checks here</div> }
                  </div>
                </div>
              }
              @if (!placed().length) { <div class="lane-empty wide-empty">Place an agent on the canvas to get a lane for its checks.</div> }
            </div>
          </div>
        </div>

        <aside class="inspector">
          @switch (selected()?.kind) {
            @case ('agent') {
              @if (agent(selectedId()); as a) {
                <div class="insp-head" [style.--agent]="color(a.id)"><mat-icon>{{ icon(a.id) }}</mat-icon><div><strong>{{ name(a.id) }}</strong><div class="muted small">{{ agentDesc(a.id)?.mandate }}</div></div></div>
                <p class="muted small">{{ agentDesc(a.id)?.description }}</p>
                <mat-slide-toggle [(ngModel)]="a.enabled" (ngModelChange)="emit()">Enabled</mat-slide-toggle>
                <div class="sec">Step execution</div>
                <mat-button-toggle-group hideSingleSelectionIndicator [value]="a.stepOrder ?? 'Parallel'" (change)="setOrder(a, $event.value)">
                  <mat-button-toggle value="Ordered">Ordered</mat-button-toggle><mat-button-toggle value="Parallel">All parallel</mat-button-toggle>
                </mat-button-toggle-group>
                <div class="sec">Runs when</div>
                @if (incoming(a.id).length) {
                  <ul class="plain">@for (t of incoming(a.id); track $index) { <li><code>{{ name(t.from) }}</code> {{ conditionLabel(t.when) }}</li> }</ul>
                  <p class="muted small">Any one holding transition is enough. Steps that need output from another agent additionally wait for that agent.</p>
                } @else { <p class="muted small">No incoming arrow – starts immediately{{ crossDeps(a.id).length ? ', after ' + crossDeps(a.id).join(', ') + ' (step dependencies)' : '' }}.</p> }
                @if (agentPlan(a.id); as ap) {
                  <div class="sec">Dry run</div>
                  <p class="small">{{ ap.enabled ? 'Stage ' + ap.stage : 'Skipped' }}{{ ap.waitsFor.length ? ' · waits for ' + ap.waitsFor.join(', ') : '' }}</p>
                  <ol class="small plain-ol">@for (st of ap.stepStages; track $index) { <li>{{ st.join(' ∥ ') }}</li> }</ol>
                }
                <div class="sec">Remove</div>
                <button mat-stroked-button color="warn" type="button" (click)="removeAgent(a.id)"><mat-icon>delete</mat-icon> Remove from canvas</button>
                <p class="muted small">Its checks go back to the palette and its arrows are dropped.</p>
              }
            }
            @case ('transition') {
              @if (transition(); as t) {
                <div class="insp-head"><mat-icon>arrow_forward</mat-icon><div><strong>{{ name(t.from) }} → {{ name(t.to) }}</strong><div class="muted small">agent transition</div></div></div>
                <div class="sec">Run {{ name(t.to) }}</div>
                <mat-button-toggle-group vertical hideSingleSelectionIndicator [(ngModel)]="t.when" (ngModelChange)="emit()" class="full">
                  @for (c of conditions; track c.value) { <mat-button-toggle [value]="c.value" [matTooltip]="c.hint">{{ c.label }}</mat-button-toggle> }
                </mat-button-toggle-group>
                <p class="muted small">{{ conditionHint(t.when) }}</p>
                <p class="muted small">“Fail” for an agent means: a step ended Failed, a stop-gate fired, or its review raised a High finding.</p>
                <div class="sec">Remove</div>
                <button mat-stroked-button color="warn" type="button" (click)="removeTransition()"><mat-icon>link_off</mat-icon> Delete arrow</button>
              }
            }
            @case ('step') {
              @if (step(selectedId()); as s) {
                <div class="insp-head" [style.--agent]="color(ownerOf(s.id) ?? '')"><mat-icon>{{ icon(ownerOf(s.id) ?? '') }}</mat-icon><div><strong>{{ describe(s.id)?.name ?? s.id }}</strong><div class="muted small"><code>{{ s.id }}</code> · {{ name(ownerOf(s.id) ?? '') }}</div></div></div>
                <p class="muted small">{{ describe(s.id)?.description }}</p>
                <mat-slide-toggle [(ngModel)]="s.enabled" (ngModelChange)="emit()">Enabled</mat-slide-toggle>
                @if (describe(s.id)?.required) { <p class="muted small"><mat-icon inline>star</mat-icon> Decision authority – disabling forces every decision to Refer.</p> }

                <div class="sec">Position</div>
                @if (owner(s.id); as o) {
                  @if ((o.stepOrder ?? 'Parallel') === 'Ordered') {
                    <mat-form-field appearance="outline" class="full" subscriptSizing="dynamic"><mat-label>Slot</mat-label>
                      <input matInput type="number" min="0" [ngModel]="s.slot" (ngModelChange)="setSlot(s, $event)" placeholder="after previous">
                      <mat-hint>Same slot as another step = run together</mat-hint>
                    </mat-form-field>
                  } @else { <p class="muted small">Lane is All parallel – position follows dependencies only.</p> }
                }
                @if (deps(s).length) {
                  <div class="badges">must run after @for (dep of deps(s); track dep) { <span class="pill small" [class.good]="isOn(dep)" [class.bad]="!isOn(dep)">{{ dep }}</span> }</div>
                } @else { <p class="muted small">Needs no other step's output.</p> }
                @for (w of stepWarnings(s.id); track w) { <div class="warn-line"><mat-icon inline>warning</mat-icon> {{ w }}</div> }

                <div class="sec">On failure</div>
                <mat-form-field appearance="outline" class="full" subscriptSizing="dynamic">
                  <mat-select [(ngModel)]="s.onFail" (ngModelChange)="emit()">@for (p of policies; track p.value) { <mat-option [value]="p.value" [matTooltip]="p.hint">{{ p.label }}</mat-option> }</mat-select>
                </mat-form-field>

                <div class="sec">Stop-gate</div>
                <mat-slide-toggle [ngModel]="!!s.stopGate" (ngModelChange)="toggleGate(s, $event)" [disabled]="!!describe(s.id)?.required">Stop the flow on this step's result</mat-slide-toggle>
                @if (describe(s.id)?.required) { <p class="muted small">Decision steps cannot carry a stop-gate.</p> }
                @if (s.stopGate; as g) {
                  <mat-form-field appearance="outline" class="full" subscriptSizing="dynamic"><mat-label>Fires when</mat-label>
                    <mat-select [(ngModel)]="g.when" (ngModelChange)="emit()">@for (t of triggers; track t.value) { <mat-option [value]="t.value">{{ t.label }}</mat-option> }</mat-select>
                  </mat-form-field>
                  @if (g.when === 'Flag') {
                    <mat-form-field appearance="outline" class="full" subscriptSizing="dynamic"><mat-label>Flag code</mat-label><input matInput [(ngModel)]="g.code" (ngModelChange)="emit()" placeholder="e.g. SANCTIONS_MATCH"></mat-form-field>
                  }
                  <mat-form-field appearance="outline" class="full" subscriptSizing="dynamic"><mat-label>Then</mat-label>
                    <mat-select [(ngModel)]="g.scope" (ngModelChange)="emit()">@for (sc of scopes; track sc.value) { <mat-option [value]="sc.value">{{ sc.label }}</mat-option> }</mat-select>
                  </mat-form-field>
                  <mat-form-field appearance="outline" class="full" subscriptSizing="dynamic"><mat-label>Decision</mat-label>
                    <mat-select [(ngModel)]="g.forceOutcome" (ngModelChange)="emit()">@for (o of outcomes; track o.value) { <mat-option [value]="o.value">{{ o.label }}</mat-option> }</mat-select>
                  </mat-form-field>
                  <p class="muted small">Required decision steps (score, terms, case) always run. The gate is recorded on the assessment and as a STOP_GATE finding.</p>
                }

                @if (describe(s.id)?.params?.length) {
                  <div class="sec">Parameters</div>
                  <mat-form-field appearance="outline" class="full" subscriptSizing="dynamic"><mat-label>Params JSON</mat-label>
                    <input matInput class="mono" [ngModel]="paramText(s)" (ngModelChange)="setParams(s, $event)" [matTooltip]="paramHelp(s.id)">
                    @if (paramError()) { <mat-hint class="text-high">{{ paramError() }}</mat-hint> }
                  </mat-form-field>
                }

                <div class="sec">Remove</div>
                <button mat-stroked-button color="warn" type="button" (click)="removeStep(s.id)" [disabled]="!!describe(s.id)?.required"><mat-icon>remove_circle_outline</mat-icon> Remove from workflow</button>
              }
            }
            @default {
              <div class="insp-head"><mat-icon>touch_app</mat-icon><div><strong>Nothing selected</strong><div class="muted small">Click an agent, an arrow or a step to configure it.</div></div></div>
              <ul class="plain small muted">
                <li>Agents with no incoming arrow start at once; several of them run in parallel.</li>
                <li>Click an arrow's label to cycle always → on success → on fail.</li>
                <li>Ordered lanes run top-down; equal slot numbers run together.</li>
                <li>↳ badges are data dependencies – they always win over slot order.</li>
                <li>A stop-gate skips remaining evidence steps and can force Refer or Decline.</li>
              </ul>
            }
          }
        </aside>
      </div>
    }
  `,
  styles: [`
    .designer { display: grid; grid-template-columns: minmax(0, 1fr) 320px; gap: 18px; align-items: start; }
    .left { min-width: 0; }
    .small { font-size: 12px; } .spacer { flex: 1; } .full { width: 100%; }
    .flow { display: grid; grid-template-columns: 170px minmax(0, 1fr); gap: 12px; }
    .palette { display: flex; flex-direction: column; gap: 8px; padding: 10px; border: 1px dashed var(--mi-border-strong, #c7ccd8); border-radius: 10px; background: var(--mi-surface-2, #f7f8fb); min-height: 120px; }
    .pal-title { font-size: 11px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; color: var(--mi-text-2); }
    .pal-item { display: flex; gap: 8px; align-items: center; padding: 8px 10px; border: 1px solid var(--mi-border, #e0e3ea); border-radius: 8px; background: var(--mi-surface, #fff); cursor: grab; font-size: 13px; user-select: none; }
    .pal-item mat-icon { color: var(--agent, var(--mi-primary, #3f51b5)); }
    .pal-item.agent-item { border-left: 4px solid var(--agent); }
    .pal-item.placeholder { opacity: .3; min-height: 36px; }
    .pal-item.required-item { border-left: 3px solid var(--mi-primary, #3f51b5); }
    .step-item mat-icon[inline] { font-size: 14px; width: 14px; height: 14px; color: var(--mi-primary, #3f51b5); margin-left: auto; }
    .canvas { position: relative; height: 340px; border: 1px solid var(--mi-border, #e0e3ea); border-radius: 10px; overflow: auto;
      background: var(--mi-surface, #fff) radial-gradient(circle, var(--mi-border, #e0e3ea) 1px, transparent 1px); background-size: 22px 22px; }
    .drop-target { position: absolute; inset: 0; border-radius: 10px; }
    .drop-target.cdk-drop-list-receiving { box-shadow: inset 0 0 0 2px var(--mi-primary, #3f51b5); }
    .canvas-empty { position: absolute; inset: 0; display: flex; gap: 8px; align-items: center; justify-content: center; color: var(--mi-text-2); font-size: 13px; padding: 40px; text-align: center; pointer-events: none; }
    .wires { position: absolute; inset: 0; pointer-events: none; color: var(--mi-text-2, #6b7280); }
    .wire { pointer-events: auto; cursor: pointer; }
    .wire .hit { stroke: transparent; stroke-width: 14; fill: none; }
    .wire .line { stroke: currentColor; stroke-width: 2; fill: none; }
    .wire.success { color: var(--mi-good, #2e7d32); } .wire.fail { color: var(--mi-bad, #c62828); } .wire.fail .line { stroke-dasharray: 6 4; }
    .wire.selected .line { stroke-width: 3.5; } .wire.selected .label-bg { stroke: currentColor; stroke-width: 1.5; }
    .wire .label-bg { fill: var(--mi-surface, #fff); stroke: var(--mi-border, #e0e3ea); }
    .wire .label { font-size: 11px; font-weight: 600; fill: currentColor; }
    .term-wire { stroke: var(--mi-text-2, #6b7280); stroke-width: 1.5; stroke-dasharray: 3 4; fill: none; color: var(--mi-text-2, #6b7280); }
    .terminal text { font-size: 10px; font-weight: 700; fill: #fff; letter-spacing: .04em; }
    .terminal .start { fill: #2e7d32; } .terminal .end { fill: #263238; } .terminal .end-inner { fill: none; stroke: #fff; stroke-width: 1.5; }
    .wire.pending { pointer-events: none; stroke: var(--mi-primary, #3f51b5); stroke-width: 2; stroke-dasharray: 4 4; fill: none; color: var(--mi-primary, #3f51b5); }
    .node { position: absolute; width: ${NODE_W}px; height: ${NODE_H}px; box-sizing: border-box; border: 1px solid var(--mi-border-strong, #c7ccd8); border-left: 5px solid var(--agent, var(--mi-primary, #3f51b5));
      border-radius: 10px; background: var(--mi-surface, #fff); box-shadow: 0 2px 6px rgba(0,0,0,.08); cursor: grab; user-select: none; }
    .node.selected { box-shadow: 0 0 0 2px var(--agent, var(--mi-primary, #3f51b5)); }
    .node.disabled { opacity: .55; border-left-color: var(--mi-text-2); }
    .node.link-target { box-shadow: 0 0 0 2px var(--mi-good, #2e7d32); }
    .node-body { display: flex; gap: 8px; align-items: center; padding: 8px 10px; height: 100%; box-sizing: border-box; }
    .node-text strong { font-size: 13px; line-height: 1.2; } .node-text span { font-size: 11px; }
    .node-body mat-icon { color: var(--agent, var(--mi-primary, #3f51b5)); }
    .node-text { display: flex; flex-direction: column; min-width: 0; } .node-text span { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .port { position: absolute; top: 50%; width: 12px; height: 12px; margin-top: -6px; border-radius: 50%; background: var(--mi-surface, #fff); border: 2px solid var(--agent, var(--mi-primary, #3f51b5)); }
    .port.in { left: -7px; } .port.out { right: -7px; cursor: crosshair; } .port.out:hover { background: var(--agent, var(--mi-primary, #3f51b5)); }
    .lanes-wrap { display: grid; grid-template-columns: 170px minmax(0, 1fr); gap: 12px; }
    .lanes { display: grid; grid-template-columns: repeat(auto-fit, minmax(230px, 1fr)); gap: 12px; }
    .lane { border: 1px solid var(--mi-border, #e0e3ea); border-top: 4px solid var(--agent, var(--mi-primary, #3f51b5)); border-radius: 10px; background: var(--mi-surface, #fff); display: flex; flex-direction: column; min-height: 200px; }
    .lane.selected { box-shadow: 0 0 0 2px var(--agent, var(--mi-primary, #3f51b5)); } .lane.disabled { opacity: .6; }
    .lane-head { display: flex; gap: 8px; align-items: center; padding: 8px 10px; border-bottom: 1px solid var(--mi-border, #e0e3ea); background: color-mix(in srgb, var(--agent, #3f51b5) 8%, var(--mi-surface, #fff)); border-radius: 6px 6px 0 0; cursor: pointer; flex-wrap: wrap; }
    .lane-head mat-icon { color: var(--agent, var(--mi-primary, #3f51b5)); }
    .step-card.grp { margin-left: 14px; position: relative; }
    .step-card.grp::before { content: ''; position: absolute; left: -12px; top: -5px; bottom: -5px; width: 7px; border: 2px solid var(--agent, #3f51b5); border-right: 0; border-radius: 0; opacity: .8; }
    .step-card.grp:not(.grp-start)::before { top: -8px; border-top: 0; border-radius: 0; }
    .step-card.grp:not(.grp-end)::before { bottom: -8px; border-bottom: 0; }
    .step-card.grp-start::before { border-top-left-radius: 6px; } .step-card.grp-end::before { border-bottom-left-radius: 6px; }
    .grp-label { position: absolute; left: -12px; top: -13px; font-size: 10px; font-weight: 700; color: var(--agent, #3f51b5); background: var(--mi-surface, #fff); padding: 0 4px; line-height: 12px; white-space: nowrap; }
    .step-card.grp-start { margin-top: 10px; }
    .notice { grid-column: 1 / -1; display: flex; gap: 6px; align-items: center; padding: 8px 12px; border-radius: 8px; font-size: 12px; background: var(--mi-warn-soft, #fff4e5); color: var(--mi-warn, #b26a00); border: 1px solid currentColor; }
    .notice mat-icon[inline] { font-size: 16px; width: 16px; height: 16px; }
    .order-toggle { transform: scale(.8); transform-origin: right center; }
    .lane-body { flex: 1; padding: 8px; display: flex; flex-direction: column; gap: 8px; min-height: 120px; }
    .lane-body.cdk-drop-list-receiving, .steps-palette.cdk-drop-list-receiving { background: var(--mi-primary-soft, #e8eaf6); }
    .lane-empty { flex: 1; display: flex; align-items: center; justify-content: center; color: var(--mi-text-2); font-size: 12px; border: 1px dashed var(--mi-border-strong, #c7ccd8); border-radius: 8px; padding: 14px; }
    .wide-empty { grid-column: 1 / -1; min-height: 120px; }
    .step-card { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 8px; padding: 8px 10px; border: 1px solid var(--mi-border, #e0e3ea); border-radius: 8px; background: var(--mi-surface, #fff); cursor: grab; font-size: 13px; user-select: none; }
    .step-card.selected { box-shadow: 0 0 0 2px var(--mi-primary, #3f51b5); }
    .step-card.off { opacity: .6; background: var(--mi-neutral-soft, #f3f4f7); }
    .step-card.required { border-left: 3px solid var(--mi-primary, #3f51b5); }
    .step-card.placeholder { opacity: .25; min-height: 44px; }
    .slot { width: 22px; height: 22px; border-radius: 50%; background: var(--mi-primary-soft, #e8eaf6); color: var(--mi-primary-deep, #283593); font-size: 11px; font-weight: 700; display: flex; align-items: center; justify-content: center; }
    .slot.hidden { visibility: hidden; width: 0; }
    .step-title { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
    .step-title mat-icon[inline] { font-size: 14px; width: 14px; height: 14px; color: var(--mi-primary, #3f51b5); }
    .pill mat-icon[inline] { font-size: 12px; width: 12px; height: 12px; vertical-align: -2px; }
    .badges { display: flex; gap: 4px; align-items: center; flex-wrap: wrap; margin-top: 4px; font-size: 12px; }
    .warn-line { display: flex; gap: 4px; align-items: flex-start; color: var(--mi-warn, #b26a00); font-size: 11px; margin-top: 4px; }
    .warn-line mat-icon[inline] { font-size: 14px; width: 14px; height: 14px; flex: 0 0 auto; }
    .inspector { position: sticky; top: 12px; padding: 14px; border: 1px solid var(--mi-border, #e0e3ea); border-radius: 10px; background: var(--mi-surface-2, #f7f8fb); display: flex; flex-direction: column; gap: 10px; }
    .insp-head { display: flex; gap: 10px; align-items: flex-start; } .insp-head mat-icon { color: var(--agent, var(--mi-primary, #3f51b5)); }
    :host-context(.fullscreen) .canvas { height: 44vh; min-height: 340px; }
    :host-context(.fullscreen) .designer { grid-template-columns: minmax(0, 1fr) 320px; }
    :host-context(.fullscreen) .flow { grid-template-columns: minmax(0, 1fr); }
    :host-context(.fullscreen) .palette:not(.steps-palette) { flex-direction: row; flex-wrap: wrap; align-items: center; min-height: 0; padding: 8px 10px; }
    :host-context(.fullscreen) .palette:not(.steps-palette) .pal-item { width: auto; }
    .wire.inferred { stroke: currentColor; stroke-width: 2; stroke-dasharray: 5 4; fill: none; opacity: .75; cursor: help; }
    :host-context(.fullscreen) .lanes { grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); }
    .sec { font-size: 11px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; color: var(--mi-text-2); margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--mi-border, #e0e3ea); }
    .plain { margin: 0; padding-left: 18px; } .plain-ol { margin: 0; padding-left: 18px; }
    .cdk-drag-preview { box-shadow: 0 8px 24px rgba(0,0,0,.18); border-radius: 10px; }
    .cdk-drag-animating { transition: transform 200ms cubic-bezier(0, 0, 0.2, 1); }
  `]
})
export class WorkflowDesignerComponent {
  readonly def = input.required<WorkflowDefinition | null>();
  readonly catalog = input<Record<string, WorkflowStepDescriptor>>({});
  readonly agentCatalog = input<Record<string, WorkflowAgentDescriptor>>({});
  readonly plan = input<WorkflowPlan | null | undefined>(null);
  /** Bump when a different definition is loaded so the canvas layout and selection reset. */
  readonly loadKey = input(0);
  readonly changed = output<void>();

  readonly policies = POLICIES;
  readonly conditions = CONDITIONS;
  readonly triggers = TRIGGERS;
  readonly scopes = SCOPES;
  readonly outcomes = OUTCOMES;
  readonly termR = TERM_R;

  private readonly canvasRef = viewChild<ElementRef<HTMLElement>>('canvas');
  readonly selected = signal<Selection>(null);
  readonly linking = signal<{ from: string; to: Point } | null>(null);
  readonly paramError = signal<string | null>(null);
  /** Short-lived explanation shown when a drop was re-ordered to respect a data dependency. */
  readonly notice = signal<string | null>(null);
  private noticeTimer: ReturnType<typeof setTimeout> | null = null;
  /** Node positions are presentation state only – the workflow JSON carries no layout. */
  private readonly moved = signal<Record<string, Point>>({});

  constructor() {
    // a newly loaded definition starts from the automatic layout with nothing selected
    effect(() => { this.loadKey(); this.moved.set({}); this.selected.set(null); }, { allowSignalWrites: true });
  }

  /** Automatic layout: one column per plan stage (catalogue order until the plan arrives), stacked within a stage. */
  private readonly autoLayout = computed<Record<string, Point>>(() => {
    const plan = this.plan(); const order = this.agentOrder();
    const stage = (id: string) => plan?.agents?.find(a => a.id === id)?.stage ?? order.indexOf(id) + 1;
    const pos: Record<string, Point> = {};
    const perStage: Record<number, number> = {};
    for (const id of this.placedIds()) {
      const st = stage(id); const row = perStage[st] ?? 0; perStage[st] = row + 1;
      pos[id] = { x: TERM_GAP + TERM_R * 2 + 16 + (st - 1) * (NODE_W + STAGE_GAP), y: 24 + row * (NODE_H + 36) };
    }
    return pos;
  });

  /** Agents nothing routes into (they start the run) and agents nothing routes out of (they finish it). */
  readonly roots = computed(() => this.terminals('start'));
  readonly sinks = computed(() => this.terminals('end'));

  /** Without explicit transitions the planner infers stages, so the first/last stage bound the run instead. */
  private terminals(side: 'start' | 'end'): WorkflowAgentConfig[] {
    const enabled = this.placed().filter(a => a.enabled);
    const t = this.def()?.transitions ?? [];
    if (t.length) return enabled.filter(a => !t.some(x => side === 'start' ? x.to === a.id : x.from === a.id));
    const stages = this.plan()?.agents ?? [];
    const stageOf = (id: string) => stages.find(p => p.id === id)?.stage;
    const known = enabled.map(a => stageOf(a.id)).filter((s): s is number => s != null);
    if (!known.length) return enabled;
    const edge = side === 'start' ? Math.min(...known) : Math.max(...known);
    return enabled.filter(a => stageOf(a.id) === edge);
  }

  readonly startPos = computed<Point>(() => {
    const ys = this.roots().map(a => this.posOf(a.id).y + NODE_H / 2);
    return { x: TERM_R + 12, y: ys.length ? ys.reduce((s, y) => s + y, 0) / ys.length : 24 + NODE_H / 2 };
  });
  readonly endPos = computed<Point>(() => {
    const maxX = Math.max(0, ...this.placed().map(a => this.posOf(a.id).x + NODE_W));
    const ys = this.sinks().map(a => this.posOf(a.id).y + NODE_H / 2);
    return { x: maxX + TERM_GAP + TERM_R, y: ys.length ? ys.reduce((s, y) => s + y, 0) / ys.length : 24 + NODE_H / 2 };
  });
  readonly canvasW = computed(() => Math.max(900, this.endPos().x + TERM_R + 24));
  readonly canvasH = computed(() => Math.max(340, ...this.placed().map(a => this.posOf(a.id).y + NODE_H + 24)));

  /** With no explicit transitions the planner sequences agents by stage; show that sequence dashed so the run is still readable end to end. */
  readonly inferredWires = computed<string[]>(() => {
    if (this.def()?.transitions?.length) return [];
    const stages = this.plan()?.agents ?? [];
    const stageOf = (id: string) => stages.find(p => p.id === id)?.stage;
    const enabled = this.placed().filter(a => a.enabled && stageOf(a.id) != null);
    const out: string[] = [];
    for (const to of enabled) {
      const prev = Math.max(-1, ...enabled.map(a => stageOf(a.id)!).filter(s => s < stageOf(to.id)!));
      if (prev < 0) continue;
      for (const from of enabled.filter(a => stageOf(a.id) === prev)) {
        const a = this.posOf(from.id); const b = this.posOf(to.id);
        out.push(this.curve(a.x + NODE_W + 7, a.y + NODE_H / 2, b.x - 7, b.y + NODE_H / 2).d);
      }
    }
    return out;
  });

  readonly terminalWires = computed<string[]>(() => {
    const s = this.startPos(); const e = this.endPos();
    const out: string[] = [];
    for (const a of this.roots()) { const p = this.posOf(a.id); out.push(this.curve(s.x + TERM_R, s.y, p.x - 7, p.y + NODE_H / 2).d); }
    for (const a of this.sinks()) { const p = this.posOf(a.id); out.push(this.curve(p.x + NODE_W + 7, p.y + NODE_H / 2, e.x - TERM_R, e.y).d); }
    return out;
  });

  readonly agentOrder = computed(() => Object.keys(this.agentCatalog()));
  readonly placed = computed(() => this.def()?.agents ?? []);
  readonly placedIds = computed(() => this.placed().map(a => a.id));
  readonly unplacedAgents = computed(() => {
    const on = new Set(this.placedIds());
    return Object.values(this.agentCatalog()).filter(a => !on.has(a.id));
  });
  readonly unplacedAgentIds = computed(() => this.unplacedAgents().map(a => a.id));
  get unplacedSteps(): string[] {
    const d = this.def(); if (!d) return [];
    const placed = new Set(d.steps.map(s => s.id));
    return Object.keys(this.catalog()).filter(id => !placed.has(id));
  }

  // ---- lookups --------------------------------------------------------------------------
  icon(id: string): string { return AGENT_ICONS[id] ?? 'smart_toy'; }
  color(id: string): string { return AGENT_COLORS[id] ?? '#3f51b5'; }

  /** Bracket position of a card inside a group of steps dispatched together: the whole lane when All parallel, equal slots when Ordered. */
  groupPos(a: WorkflowAgentConfig, i: number): 'start' | 'mid' | 'end' | null {
    const n = a.steps.length;
    if (n < 2) return null;
    const key = (k: number) => (a.stepOrder ?? 'Parallel') === 'Ordered' ? this.slotOf(a, k) : 0;
    const same = (k: number) => k >= 0 && k < n && key(k) === key(i);
    const prev = same(i - 1), next = same(i + 1);
    if (!prev && !next) return null;
    return !prev ? 'start' : !next ? 'end' : 'mid';
  }
  name(id: string): string { return this.agentCatalog()[id]?.name ?? id; }
  agentDesc(id: string): WorkflowAgentDescriptor | undefined { return this.agentCatalog()[id]; }
  describe(id: string): WorkflowStepDescriptor | undefined { return this.catalog()[id]; }
  agent(id: string | null): WorkflowAgentConfig | undefined { return id ? this.placed().find(a => a.id === id) : undefined; }
  step(id: string | null): WorkflowStepConfig | undefined { return id ? this.def()?.steps.find(s => s.id === id) : undefined; }
  owner(stepId: string): WorkflowAgentConfig | undefined { return this.placed().find(a => a.steps.includes(stepId)); }
  ownerOf(stepId: string): string | null { return this.owner(stepId)?.id ?? null; }
  isOn(id: string): boolean { const s = this.step(id); const o = this.owner(id); return !!s?.enabled && (o?.enabled ?? false); }
  deps(s: WorkflowStepConfig): string[] { return s.dependsOn ?? this.describe(s.id)?.dependsOn ?? []; }
  agentPlan(id: string) { return this.plan()?.agents?.find(a => a.id === id) ?? null; }
  transition(): WorkflowTransition | undefined { const sel = this.selected(); return sel?.kind === 'transition' ? this.def()?.transitions?.[sel.index] : undefined; }
  incoming(agentId: string): WorkflowTransition[] { return (this.def()?.transitions ?? []).filter(t => t.to === agentId); }
  selectedId(): string | null { const sel = this.selected(); return sel && sel.kind !== 'transition' ? sel.id : null; }
  conditionLabel(c: TransitionCondition): string { return CONDITIONS.find(x => x.value === c)?.label ?? c; }
  conditionHint(c: TransitionCondition): string { return CONDITIONS.find(x => x.value === c)?.hint ?? ''; }
  isSelected(kind: 'agent' | 'step', id: string): boolean;
  isSelected(kind: 'transition', index: number): boolean;
  isSelected(kind: string, key: string | number): boolean {
    const sel = this.selected(); if (!sel || sel.kind !== kind) return false;
    return sel.kind === 'transition' ? sel.index === key : sel.id === key;
  }

  agentSummary(a: WorkflowAgentConfig): string {
    const on = a.steps.filter(id => this.step(id)?.enabled).length;
    return `${on}/${a.steps.length} checks · ${(a.stepOrder ?? 'Parallel') === 'Ordered' ? '↓ ordered' : '∥ parallel'}`;
  }

  /** Agents whose steps this agent's steps depend on (dependency-implied waits, shown when no arrows exist). */
  crossDeps(agentId: string): string[] {
    const a = this.agent(agentId); if (!a) return [];
    const others = new Set<string>();
    for (const id of a.steps) for (const dep of this.deps(this.step(id) ?? { id, enabled: true, onFail: 'Skip' })) {
      const o = this.ownerOf(dep); if (o && o !== agentId) others.add(this.name(o));
    }
    return [...others];
  }

  /** Plan warnings that mention this step. */
  stepWarnings(id: string): string[] { return (this.plan()?.warnings ?? []).filter(w => w.includes(`'${id}'`) && !w.startsWith('Stop-gate')); }

  stepStage(agentId: string, stepId: string): string | null {
    const ap = this.agentPlan(agentId); if (!ap?.enabled) return null;
    const i = ap.stepStages.findIndex(st => st.includes(stepId));
    return i < 0 ? null : `${i + 1}/${ap.stepStages.length}`;
  }

  slotOf(a: WorkflowAgentConfig, index: number): number {
    let prev = -1;
    for (let i = 0; i <= index; i++) { const s = this.step(a.steps[i]); prev = s?.slot ?? prev + 1; }
    return prev;
  }

  // ---- selection ------------------------------------------------------------------------
  select(sel: Selection): void { this.selected.set(sel); this.paramError.set(null); }
  pick(sel: Selection, e: Event): void { e.stopPropagation(); this.select(sel); }
  emit(): void { this.changed.emit(); }

  // ---- canvas: agents & transitions ----------------------------------------------------
  /** Node positions are presentation state only – the workflow JSON carries no layout. */
  posOf(id: string): Point { return this.moved()[id] ?? this.autoLayout()[id] ?? { x: 30, y: 30 }; }

  nodeMoved(id: string, e: CdkDragEnd): void {
    const p = e.source.getFreeDragPosition();
    this.moved.update(all => ({ ...all, [id]: { x: p.x, y: p.y } }));
  }

  dropAgent(e: CdkDragDrop<string[]>): void {
    const d = this.def(); if (!d) return;
    if (e.previousContainer === e.container) return; // free drag inside the canvas is handled by nodeMoved
    const id = e.item.data as string;
    if (this.agent(id)) return;
    const rect = this.canvasRef()?.nativeElement.getBoundingClientRect();
    const x = rect ? Math.max(0, Math.min(rect.width - NODE_W, e.dropPoint.x - rect.left - NODE_W / 2)) : 30;
    const y = rect ? Math.max(0, Math.min(rect.height - NODE_H, e.dropPoint.y - rect.top - NODE_H / 2)) : 30;
    this.moved.update(all => ({ ...all, [id]: { x, y } }));
    d.agents ??= [];
    d.agents.push({ id, enabled: true, steps: [], stepOrder: 'Parallel' });
    d.agents.sort((a, b) => this.agentOrder().indexOf(a.id) - this.agentOrder().indexOf(b.id));
    this.select({ kind: 'agent', id });
    this.emit();
  }

  removeAgent(id: string): void {
    const d = this.def(); const a = this.agent(id); if (!d || !a) return;
    const gone = new Set(a.steps);
    d.steps = d.steps.filter(s => !gone.has(s.id));
    d.agents = (d.agents ?? []).filter(x => x.id !== id);
    d.transitions = (d.transitions ?? []).filter(t => t.from !== id && t.to !== id);
    this.select(null);
    this.emit();
  }

  startLink(from: string, e: MouseEvent): void {
    e.stopPropagation(); e.preventDefault();
    const p = this.posOf(from);
    this.linking.set({ from, to: { x: p.x + NODE_W, y: p.y + NODE_H / 2 } });
  }

  trackLink(e: MouseEvent): void {
    const l = this.linking(); const rect = this.canvasRef()?.nativeElement.getBoundingClientRect();
    if (!l || !rect) return;
    this.linking.set({ from: l.from, to: { x: e.clientX - rect.left, y: e.clientY - rect.top } });
  }

  finishLink(to: string, e: MouseEvent): void {
    const l = this.linking(); if (!l) return;
    e.stopPropagation();
    this.linking.set(null);
    const d = this.def(); if (!d || l.from === to) return;
    d.transitions ??= [];
    if (d.transitions.some(t => t.from === l.from && t.to === to)) return;
    d.transitions.push({ from: l.from, to, when: 'Always' });
    this.select({ kind: 'transition', index: d.transitions.length - 1 });
    this.emit();
  }

  cancelLink(): void { if (this.linking()) this.linking.set(null); }

  cycle(index: number, e: Event): void {
    e.stopPropagation();
    const t = this.def()?.transitions?.[index]; if (!t) return;
    const order: TransitionCondition[] = ['Always', 'Success', 'Fail'];
    t.when = order[(order.indexOf(t.when) + 1) % order.length];
    this.select({ kind: 'transition', index });
    this.emit();
  }

  removeTransition(): void {
    const sel = this.selected(); const d = this.def();
    if (!d?.transitions || sel?.kind !== 'transition') return;
    d.transitions.splice(sel.index, 1);
    this.select(null);
    this.emit();
  }

  wire(t: WorkflowTransition): { d: string; mx: number; my: number } | null {
    if (!this.agent(t.from) || !this.agent(t.to)) return null;
    const a = this.posOf(t.from); const b = this.posOf(t.to);
    const x1 = a.x + NODE_W; const y1 = a.y + NODE_H / 2; const x2 = b.x; const y2 = b.y + NODE_H / 2;
    return this.curve(x1, y1, x2, y2);
  }

  pendingWire(l: { from: string; to: Point }): string {
    const a = this.posOf(l.from);
    return this.curve(a.x + NODE_W, a.y + NODE_H / 2, l.to.x, l.to.y).d;
  }

  private curve(x1: number, y1: number, x2: number, y2: number): { d: string; mx: number; my: number } {
    const dx = Math.max(40, Math.abs(x2 - x1) / 2);
    const d = x2 >= x1
      ? `M${x1},${y1} C${x1 + dx},${y1} ${x2 - dx},${y2} ${x2},${y2}`
      : `M${x1},${y1} C${x1 + 60},${y1} ${x1 + 60},${(y1 + y2) / 2} ${(x1 + x2) / 2},${(y1 + y2) / 2} S${x2 - 60},${y2} ${x2},${y2}`;
    return { d, mx: (x1 + x2) / 2, my: (y1 + y2) / 2 };
  }

  // ---- lanes: steps ---------------------------------------------------------------------
  laneId(agentId: string): string { return `lane-${agentId}`; }
  laneIds(except?: string): string[] { return [...this.placed().filter(a => a.id !== except).map(a => this.laneId(a.id)), 'step-palette']; }

  setOrder(a: WorkflowAgentConfig, order: AgentStepOrder): void {
    a.stepOrder = order;
    if (order === 'Parallel') for (const id of a.steps) { const s = this.step(id); if (s) s.slot = null; }
    this.emit();
  }

  dropStep(e: CdkDragDrop<string[]>): void {
    const d = this.def(); if (!d) return;
    const id = e.item.data as string;
    const fromPalette = e.previousContainer.id === 'step-palette';
    const toPalette = e.container.id === 'step-palette';
    if (fromPalette && toPalette) return;
    if (fromPalette) {
      const desc = this.describe(id);
      d.steps.push({ id, enabled: true, onFail: desc?.required ? 'Refer' : 'Skip', slot: null, stopGate: null });
      e.container.data.splice(e.currentIndex, 0, id);
    } else if (toPalette) {
      if (this.describe(id)?.required) return;
      this.removeStep(id); return;
    } else if (e.previousContainer === e.container) {
      moveItemInArray(e.container.data, e.previousIndex, e.currentIndex);
    } else {
      transferArrayItem(e.previousContainer.data, e.container.data, e.previousIndex, e.currentIndex);
      const s = this.step(id); if (s) s.slot = null;
    }
    const lane = this.placed().find(a => this.laneId(a.id) === e.container.id);
    if (lane) this.enforceDependencyOrder(lane, id);
    this.select({ kind: 'step', id });
    this.emit();
  }

  /**
   * A step can never sit above a lane-mate whose output it needs (nor below one that needs its output).
   * Re-orders the lane with a stable topological sort and explains the move when the dropped step was affected.
   */
  private enforceDependencyOrder(lane: WorkflowAgentConfig, droppedId: string): void {
    const inLane = new Set(lane.steps);
    const depsOf = (id: string) => this.deps(this.step(id) ?? { id, enabled: true, onFail: 'Skip' }).filter(d => inLane.has(d));
    const before = lane.steps.indexOf(droppedId);
    const sorted: string[] = []; const visiting = new Set<string>();
    const visit = (id: string) => {
      if (sorted.includes(id) || visiting.has(id)) return;
      visiting.add(id);
      for (const d of depsOf(id)) visit(d);
      visiting.delete(id); sorted.push(id);
    };
    for (const id of lane.steps) visit(id);
    if (sorted.every((id, i) => id === lane.steps[i])) return;
    lane.steps.splice(0, lane.steps.length, ...sorted);
    const after = lane.steps.indexOf(droppedId);
    const label = (id: string) => this.describe(id)?.name ?? id;
    const needs = depsOf(droppedId);
    const neededBy = lane.steps.filter(id => depsOf(id).includes(droppedId));
    const why = after > before && needs.length
      ? `${label(droppedId)} needs the output of ${needs.map(label).join(', ')}, so it was moved below.`
      : neededBy.length ? `${neededBy.map(label).join(', ')} need${neededBy.length === 1 ? 's' : ''} the output of ${label(droppedId)}, so it was moved above.` : `Order adjusted to respect data dependencies.`;
    this.showNotice(why);
  }

  private showNotice(text: string): void {
    this.notice.set(text);
    if (this.noticeTimer) clearTimeout(this.noticeTimer);
    this.noticeTimer = setTimeout(() => this.notice.set(null), 6000);
  }

  removeStep(id: string): void {
    const d = this.def(); if (!d) return;
    d.steps = d.steps.filter(s => s.id !== id);
    for (const a of d.agents ?? []) a.steps = a.steps.filter(x => x !== id);
    for (const s of d.steps) if (s.dependsOn) s.dependsOn = s.dependsOn.filter(x => x !== id);
    if (this.selectedId() === id) this.select(null);
    this.emit();
  }

  setSlot(s: WorkflowStepConfig, value: number | string | null): void {
    s.slot = value === null || value === '' ? null : Math.max(0, Number(value));
    this.emit();
  }

  toggleGate(s: WorkflowStepConfig, on: boolean): void {
    s.stopGate = on ? { when: 'Failed', scope: 'Agent', forceOutcome: 'None', code: null } : null;
    this.emit();
  }

  paramText(s: WorkflowStepConfig): string { return s.params && Object.keys(s.params).length ? JSON.stringify(s.params) : ''; }
  paramHelp(id: string): string { return (this.describe(id)?.params ?? []).map(p => `${p.name} (${p.type}, default ${p.default}): ${p.description}`).join('\n'); }
  setParams(s: WorkflowStepConfig, text: string): void {
    if (!text.trim()) { s.params = null; this.paramError.set(null); this.emit(); return; }
    try { s.params = JSON.parse(text) as Record<string, unknown>; this.paramError.set(null); this.emit(); }
    catch { this.paramError.set('Not valid JSON – kept the previous value.'); }
  }
}
