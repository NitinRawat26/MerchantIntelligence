import { Component, ElementRef, HostListener, computed, inject, signal, viewChild } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { forkJoin } from 'rxjs';
import { debounceTime, Subject, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { WorkflowAgentConfig, WorkflowAgentDescriptor, WorkflowDefinition, WorkflowPlan, WorkflowStepDescriptor, WorkflowVersion } from '../../shared/models';
import { FieldHintComponent, StatusComponent } from '../../shared/ui';
import { AGENT_ICONS, WorkflowDesignerComponent } from './workflow-designer.component';

@Component({
  selector: 'mi-workflows',
  standalone: true,
  imports: [FormsModule, DatePipe, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSlideToggleModule, MatTableModule, MatTabsModule, MatTooltipModule, StatusComponent, FieldHintComponent, WorkflowDesignerComponent],
  styleUrl: './platform.scss',
  template: `
    <div class="page">
      <mat-card appearance="outlined">
        <mat-card-header>
          <mat-icon mat-card-avatar>account_tree</mat-icon>
          <mat-card-title>Assessment workflows</mat-card-title>
          <mat-card-subtitle>Four rule-based agents own the checks the Full Assessment runs · design which agent runs after which, under what condition, and how each agent orders its checks · executed as a Microsoft Agent Framework workflow, no model involved</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <mi-status [loading]="loading()" [error]="error()"></mi-status>
          <mat-tab-group>
            <mat-tab label="Designer">
              <div #designerTab class="tab" [class.fullscreen]="fullscreen()">
                <div class="row toolbar">
                  <button mat-stroked-button type="button" (click)="toggleFullscreen()" [matTooltip]="fullscreen() ? 'Back to the page (Esc)' : 'Expand the designer over the whole window'"><mat-icon>{{ fullscreen() ? 'fullscreen_exit' : 'fullscreen' }}</mat-icon> {{ fullscreen() ? 'Exit full screen' : 'Full screen' }}</button>
                  <button mat-stroked-button type="button" (click)="load()" matTooltip="Discard edits and load the active version"><mat-icon>refresh</mat-icon> Reload active</button>
                  <button mat-stroked-button type="button" (click)="resetToDefault()" matTooltip="Load the built-in default flow"><mat-icon>restart_alt</mat-icon> Default flow</button>
                  <button mat-stroked-button type="button" (click)="newWorkflow()" matTooltip="Start from an empty canvas"><mat-icon>add</mat-icon> New workflow</button>
                  <span class="spacer"></span>
                  <mat-form-field appearance="outline" class="w200" subscriptSizing="dynamic"><mat-label>Author</mat-label><input matInput [(ngModel)]="author" required><mi-field-hint matSuffix for="workflowAuthor"></mi-field-hint></mat-form-field>
                  <mat-form-field appearance="outline" class="w300" subscriptSizing="dynamic"><mat-label>Comment</mat-label><input matInput [(ngModel)]="comment"><mi-field-hint matSuffix for="workflowComment"></mi-field-hint></mat-form-field>
                  <button mat-flat-button color="primary" type="button" (click)="publish()" [disabled]="!author || !validation()?.valid"><mat-icon>publish</mat-icon> Publish &amp; activate</button>
                </div>
                @if (message(); as m) { <p [class]="m.ok ? 'text-low' : 'text-high'">{{ m.text }}</p> }

                @if (draft(); as d) {
                  <div class="status-line">
                    <span class="muted">editing from <strong>{{ source() }}</strong></span>
                    @if (validation(); as v) {
                      @if (v.valid) { <span class="pill small good"><mat-icon inline>check_circle</mat-icon> valid · {{ v.plan?.stages?.length }} stage(s) · {{ enabledCount() }} of {{ d.steps.length }} checks enabled</span> }
                      @else { <span class="pill small bad"><mat-icon inline>error</mat-icon> {{ v.error }}</span> }
                      @if (v.plan?.warnings?.length) { <span class="pill small warn" [matTooltip]="v.plan!.warnings.join('\\n')"><mat-icon inline>warning</mat-icon> {{ v.plan!.warnings.length }} warning(s)</span> }
                    }
                  </div>
                  <div class="grid-3">
                    <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>Name</mat-label><input matInput [(ngModel)]="d.name" (ngModelChange)="changed()"><mi-field-hint matSuffix for="workflowName"></mi-field-hint></mat-form-field>
                    <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>Description</mat-label><input matInput [(ngModel)]="d.description" (ngModelChange)="changed()"><mi-field-hint matSuffix for="workflowDescription"></mi-field-hint></mat-form-field>
                    <div class="row"><mat-slide-toggle [(ngModel)]="d.haltOnHardStop" (ngModelChange)="changed()">Halt evidence steps on hard stop</mat-slide-toggle><mi-field-hint for="workflowHaltOnHardStop"></mi-field-hint></div>
                  </div>

                  <mi-workflow-designer [def]="d" [catalog]="catalog()" [agentCatalog]="agentCatalog()" [plan]="validation()?.plan" [loadKey]="loadKey()" (changed)="changed()"></mi-workflow-designer>
                }
              </div>
            </mat-tab>

            <mat-tab label="Dry run">
              <div class="tab two-col">
                <div>
                  <h3>Validation</h3>
                  @if (validation(); as v) {
                    @if (v.valid) {
                      <p class="text-low"><mat-icon inline>check_circle</mat-icon> Valid — {{ v.plan?.stages?.length }} stage(s), {{ enabledCount() }} enabled step(s).</p>
                    } @else {
                      <p class="text-high"><mat-icon inline>error</mat-icon> {{ v.error }}</p>
                    }
                    @if (v.plan?.warnings?.length) {
                      <h3>Warnings</h3>
                      <ul class="rule-list">@for (w of v.plan!.warnings; track w) { <li><mat-icon inline class="text-medium">warning</mat-icon> {{ w }}</li> }</ul>
                    }
                    @if (v.plan?.disabled?.length) {
                      <h3>Coverage gaps</h3>
                      <p class="muted">These steps are recorded as Skipped on every assessment: @for (dd of v.plan!.disabled; track dd) { <code>{{ dd }}</code> }</p>
                    }
                  } @else { <p class="empty">Edit the workflow to see validation.</p> }

                  <h3>Agent order <span class="muted">agents in the same stage run concurrently</span></h3>
                  @if (validation()?.plan; as p) {
                    <ol class="stages">
                      @for (a of p.agents; track a.id) {
                        <li>
                          <span class="pill small" [class.good]="a.enabled" [class.warn]="!a.enabled"><mat-icon inline>{{ agentIcon(a.id) }}</mat-icon> {{ a.name }}</span>
                          <span class="muted small">
                            {{ a.enabled ? 'stage ' + a.stage : 'skipped' }}{{ a.waitsFor.length ? ' · after ' + a.waitsFor.join(', ') : '' }}
                            @if (a.runsWhen.length) { · runs when @for (t of a.runsWhen; track $index) { <code>{{ t.from }}</code> {{ t.when === 'Always' ? 'finished' : t.when === 'Success' ? 'succeeded' : 'failed' }} } }
                          </span>
                          <div class="sub-stages">@for (st of a.stepStages; track $index; let i = $index) { <span class="pill small neutral">{{ i + 1 }}: {{ st.join(' ∥ ') }}</span> }</div>
                        </li>
                      }
                    </ol>
                  }

                  <h3>Step stages <span class="muted">steps in the same stage run concurrently</span></h3>
                  @if (validation()?.plan; as p) {
                    <ol class="stages">
                      @for (st of p.stages; track st.index) { <li>@for (id of st.steps; track id) { <span class="pill small good">{{ describe(id)?.name ?? id }}</span> }</li> }
                    </ol>
                  }
                </div>
                <div>
                  <h3>Graph <span class="muted">Mermaid, as built for the Agent Framework runner</span></h3>
                  @if (validation()?.plan; as p) { <pre class="mono graph">{{ p.mermaid }}</pre> } @else { <p class="empty">No plan yet.</p> }
                </div>
              </div>
            </mat-tab>

            <mat-tab label="JSON">
              <div class="tab">
                <p class="muted">The definition exactly as it is validated and published. Edit and apply to make bulk changes; the designer reflects the result.</p>
                <textarea class="mono json" [ngModel]="jsonText()" (ngModelChange)="jsonDraft = $event" spellcheck="false"></textarea>
                <div class="row">
                  <button mat-stroked-button type="button" (click)="applyJson()" [disabled]="!jsonDraft"><mat-icon>done</mat-icon> Apply JSON</button>
                  @if (jsonError(); as e) { <span class="text-high">{{ e }}</span> }
                </div>
              </div>
            </mat-tab>

            <mat-tab label="Version history">
              <div class="tab">
                <table mat-table [dataSource]="history()" class="full">
                  <ng-container matColumnDef="version"><th mat-header-cell *matHeaderCellDef>Version</th><td mat-cell *matCellDef="let h">v{{ h.version }} @if (h.active) {<span class="pill small good">active</span>}</td></ng-container>
                  <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Name</th><td mat-cell *matCellDef="let h">{{ h.name }}</td></ng-container>
                  <ng-container matColumnDef="author"><th mat-header-cell *matHeaderCellDef>Author</th><td mat-cell *matCellDef="let h">{{ h.author }}</td></ng-container>
                  <ng-container matColumnDef="comment"><th mat-header-cell *matHeaderCellDef>Comment</th><td mat-cell *matCellDef="let h">{{ h.comment }}</td></ng-container>
                  <ng-container matColumnDef="steps"><th mat-header-cell *matHeaderCellDef>Steps</th><td mat-cell *matCellDef="let h">{{ h.enabledSteps }} / {{ h.totalSteps }}</td></ng-container>
                  <ng-container matColumnDef="created"><th mat-header-cell *matHeaderCellDef>Created</th><td mat-cell *matCellDef="let h">{{ h.createdAt | date:'short' }}</td></ng-container>
                  <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th><td mat-cell *matCellDef="let h">
                    <button mat-button type="button" (click)="view(h.version)">Load into designer</button>
                    @if (!h.active) { <button mat-button type="button" color="warn" (click)="rollback(h.version)">Roll back</button> }
                  </td></ng-container>
                  <tr mat-header-row *matHeaderRowDef="historyColumns"></tr>
                  <tr mat-row *matRowDef="let row; columns: historyColumns"></tr>
                </table>
                @if (!history().length) { <p class="empty">Only the built-in default workflow exists; publish to create version history.</p> }
              </div>
            </mat-tab>
          </mat-tab-group>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`
    .tab { padding: 18px 0 4px; } .w200 { width: 200px; } .w300 { width: 300px; } .spacer { flex: 1; } .small { font-size: 12px; }
    .toolbar { margin-bottom: 10px; }
    .tab.fullscreen { overflow: auto; padding: 14px 22px 22px; background: var(--mi-bg, #f4f6fa); }
    .status-line { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; margin: 4px 0 12px; }
    .pill mat-icon[inline] { font-size: 14px; width: 14px; height: 14px; vertical-align: -2px; }
    .stages { padding-left: 20px; display: flex; flex-direction: column; gap: 8px; } .stages li { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; }
    .sub-stages { display: flex; gap: 4px; flex-wrap: wrap; width: 100%; }
    .graph { white-space: pre; overflow: auto; max-height: 480px; padding: 12px; border-radius: 8px; background: var(--mi-neutral-soft, #f3f4f7); font-size: 12px; }
    .json { width: 100%; min-height: 420px; box-sizing: border-box; padding: 12px; border: 1px solid var(--mi-border, #e0e3ea); border-radius: 8px; background: var(--mi-neutral-soft, #f3f4f7); font-size: 12px; resize: vertical; }
  `]
})
export class WorkflowsComponent {
  private readonly api = inject(SuiteApiService);
  readonly historyColumns = ['version', 'name', 'author', 'comment', 'steps', 'created', 'actions'];

  author = 'policy-admin';
  comment = '';
  jsonDraft = '';

  readonly catalog = signal<Record<string, WorkflowStepDescriptor>>({});
  readonly agentCatalog = signal<Record<string, WorkflowAgentDescriptor>>({});
  readonly draft = signal<WorkflowDefinition | null>(null);
  readonly source = signal('active');
  readonly validation = signal<{ valid: boolean; error?: string | null; plan?: WorkflowPlan | null } | null>(null);
  readonly history = signal<WorkflowVersion[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<{ ok: boolean; text: string } | null>(null);
  readonly jsonError = signal<string | null>(null);
  readonly loadKey = signal(0);
  readonly fullscreen = signal(false);
  private readonly designerTab = viewChild<ElementRef<HTMLElement>>('designerTab');

  /** Browser fullscreen on the designer tab: fills the window over the sidebar; Esc or the button leaves it. */
  toggleFullscreen(): void {
    if (document.fullscreenElement) { void document.exitFullscreen(); return; }
    void this.designerTab()?.nativeElement.requestFullscreen();
  }
  @HostListener('document:fullscreenchange') syncFullscreen(): void { this.fullscreen.set(document.fullscreenElement === this.designerTab()?.nativeElement); }
  readonly enabledCount = computed(() => this.draft()?.steps.filter(s => s.enabled).length ?? 0);
  readonly jsonText = computed(() => { const d = this.draft(); return d ? JSON.stringify(d, null, 2) : ''; });

  private readonly validate$ = new Subject<WorkflowDefinition>();

  constructor() {
    this.validate$.pipe(debounceTime(250), switchMap(d => this.api.validateWorkflow(d)), takeUntilDestroyed())
      .subscribe({ next: v => this.validation.set(v), error: e => this.validation.set({ valid: false, error: describeError(e) }) });
    this.load();
  }

  load(): void {
    this.loading.set(true); this.error.set(null); this.message.set(null);
    forkJoin({ catalog: this.api.workflowCatalog(), agents: this.api.workflowAgents(), active: this.api.workflowActive(), history: this.api.workflowHistory() }).subscribe({
      next: r => {
        this.catalog.set(Object.fromEntries(r.catalog.map(c => [c.id, c])));
        this.agentCatalog.set(Object.fromEntries(r.agents.map(a => [a.id, a])));
        this.history.set(r.history);
        this.setDraft(r.active, `active (v${r.active.version})`);
        this.loading.set(false);
      },
      error: e => { this.error.set(describeError(e)); this.loading.set(false); }
    });
  }

  resetToDefault(): void {
    this.api.workflowDefault().subscribe({ next: d => this.setDraft(d, 'built-in default'), error: e => this.error.set(describeError(e)) });
  }

  /** Empty canvas: no agents placed, no checks, no arrows – the designer builds everything up from the palettes. */
  newWorkflow(): void {
    this.setDraft({ name: 'New workflow', version: '1', description: null, haltOnHardStop: true, steps: [], agents: [], transitions: [] }, 'blank');
    this.message.set(null);
  }

  private setDraft(def: WorkflowDefinition, source: string): void {
    const copy = structuredClone(def);
    // older versions have no agent section: materialise the catalog defaults so ownership is visible and editable
    copy.agents ??= Object.values(this.agentCatalog()).map<WorkflowAgentConfig>(a => ({ id: a.id, enabled: true, steps: [...a.defaultSteps], stepOrder: 'Parallel' }));
    for (const a of copy.agents) a.stepOrder ??= 'Parallel';
    copy.transitions ??= [];
    this.draft.set(copy);
    this.source.set(source);
    this.loadKey.update(k => k + 1);
    this.jsonDraft = '';
    this.jsonError.set(null);
    this.changed();
  }

  changed(): void {
    const d = this.draft(); if (!d) return;
    this.draft.set({ ...d, steps: [...d.steps], agents: d.agents ? [...d.agents] : d.agents, transitions: d.transitions ? [...d.transitions] : d.transitions });
    this.validate$.next(d);
  }

  applyJson(): void {
    try {
      const parsed = JSON.parse(this.jsonDraft) as WorkflowDefinition;
      if (!parsed || !Array.isArray(parsed.steps)) throw new Error('"steps" must be an array');
      this.setDraft(parsed, 'JSON');
    } catch (e) { this.jsonError.set(`Not a valid workflow definition: ${e instanceof Error ? e.message : String(e)}`); }
  }

  describe(id: string): WorkflowStepDescriptor | undefined { return this.catalog()[id]; }
  agentIcon(id: string): string { return AGENT_ICONS[id] ?? 'smart_toy'; }

  publish(): void {
    const d = this.draft(); if (!d) return;
    this.api.publishWorkflow(d, this.author, this.comment || undefined).subscribe({
      next: v => { this.message.set({ ok: true, text: `Published and activated v${v.version} · ${v.enabledSteps}/${v.totalSteps} steps enabled` }); this.comment = ''; this.load(); },
      error: e => this.message.set({ ok: false, text: describeError(e) })
    });
  }

  view(version: number): void {
    this.api.workflowVersion(version).subscribe({
      next: d => { this.setDraft(d, `v${version} (not active)`); this.message.set({ ok: true, text: `Loaded v${version} into the designer — publish to activate it` }); },
      error: e => this.error.set(describeError(e))
    });
  }

  rollback(version: number): void {
    this.api.rollbackWorkflow(version, this.author).subscribe({
      next: v => { this.message.set({ ok: true, text: `Rolled back → active v${v.version}` }); this.load(); },
      error: e => this.message.set({ ok: false, text: describeError(e) })
    });
  }
}
