import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { CdkDragDrop, DragDropModule, moveItemInArray } from '@angular/cdk/drag-drop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { forkJoin } from 'rxjs';
import { debounceTime, Subject, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { StepFailurePolicy, WorkflowDefinition, WorkflowPlan, WorkflowStepConfig, WorkflowStepDescriptor, WorkflowVersion } from '../../shared/models';
import { FieldHintComponent, StatusComponent } from '../../shared/ui';

const POLICIES: { value: StepFailurePolicy; label: string; hint: string }[] = [
  { value: 'Skip', label: 'Skip (coverage gap)', hint: 'Failed check becomes a coverage gap.' },
  { value: 'Refer', label: 'Refer (no Approve)', hint: 'Coverage gap, and the decision cannot be Approve.' },
  { value: 'Abort', label: 'Abort assessment', hint: 'The whole assessment fails.' }
];

@Component({
  selector: 'mi-workflows',
  standalone: true,
  imports: [FormsModule, DatePipe, DragDropModule, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule,
    MatSlideToggleModule, MatTableModule, MatTabsModule, MatTooltipModule, StatusComponent, FieldHintComponent],
  styleUrl: './platform.scss',
  template: `
    <div class="page">
      <mat-card appearance="outlined">
        <mat-card-header>
          <mat-icon mat-card-avatar>account_tree</mat-icon>
          <mat-card-title>Assessment workflows</mat-card-title>
          <mat-card-subtitle>Which checks the Full Assessment runs, in what order, and what happens when one fails · executed as a Microsoft Agent Framework workflow</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <mi-status [loading]="loading()" [error]="error()"></mi-status>
          <mat-tab-group>
            <mat-tab label="Editor">
              <div class="tab">
                <div class="row">
                  <button mat-stroked-button type="button" (click)="load()"><mat-icon>refresh</mat-icon> Reload active</button>
                  <button mat-stroked-button type="button" (click)="resetToDefault()"><mat-icon>restart_alt</mat-icon> Reset to default</button>
                  <mat-form-field appearance="outline" class="w200"><mat-label>Author</mat-label><input matInput [(ngModel)]="author" required><mi-field-hint matSuffix for="workflowAuthor"></mi-field-hint></mat-form-field>
                  <mat-form-field appearance="outline" class="w300"><mat-label>Comment</mat-label><input matInput [(ngModel)]="comment"><mi-field-hint matSuffix for="workflowComment"></mi-field-hint></mat-form-field>
                  <button mat-flat-button color="primary" type="button" (click)="publish()" [disabled]="!author || !validation()?.valid"><mat-icon>publish</mat-icon> Publish &amp; activate</button>
                </div>
                @if (message(); as m) { <p [class]="m.ok ? 'text-low' : 'text-high'">{{ m.text }}</p> }

                @if (draft(); as d) {
                  <h3>Definition <span class="muted">editing from {{ source() }}</span></h3>
                  <div class="grid-3">
                    <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput [(ngModel)]="d.name" (ngModelChange)="changed()"><mi-field-hint matSuffix for="workflowName"></mi-field-hint></mat-form-field>
                    <mat-form-field appearance="outline"><mat-label>Description</mat-label><input matInput [(ngModel)]="d.description" (ngModelChange)="changed()"><mi-field-hint matSuffix for="workflowDescription"></mi-field-hint></mat-form-field>
                    <div class="row"><mat-slide-toggle [(ngModel)]="d.haltOnHardStop" (ngModelChange)="changed()">Halt evidence steps on hard stop</mat-slide-toggle><mi-field-hint for="workflowHaltOnHardStop"></mi-field-hint></div>
                  </div>

                  <h3>Steps <span class="muted">drag to reorder · {{ enabledCount() }} of {{ d.steps.length }} enabled</span></h3>
                  <div class="steps" cdkDropList (cdkDropListDropped)="drop($event)">
                    @for (s of d.steps; track s.id; let i = $index) {
                      <div class="step" cdkDrag [class.disabled]="!s.enabled" [class.blocked]="blocked(s.id)" [class.required]="describe(s.id)?.required">
                        <div class="handle" cdkDragHandle matTooltip="Drag to reorder"><mat-icon>drag_indicator</mat-icon><span class="num">{{ i + 1 }}</span></div>
                        <div class="body">
                          <div class="title-row">
                            <strong>{{ describe(s.id)?.name ?? s.id }}</strong> <code class="muted">{{ s.id }}</code>
                            @if (describe(s.id)?.required) { <span class="pill small neutral" matTooltip="Disabling this step forces every decision to Refer">decision authority</span> }
                            @if (stageOf(s.id); as st) { <span class="pill small good">stage {{ st }}</span> }
                            @if (!s.enabled) { <span class="pill small warn">skipped · coverage gap</span> }
                            @if (blocked(s.id)) { <span class="pill small bad" matTooltip="An enabled dependency is disabled or ordered after this step">degraded</span> }
                          </div>
                          <div class="muted">{{ describe(s.id)?.description }}</div>
                          @if (describe(s.id)?.dependsOn?.length) {
                            <div class="deps muted">needs
                              @for (dep of describe(s.id)!.dependsOn; track dep) { <span class="pill small" [class.good]="isOn(dep)" [class.bad]="!isOn(dep)">{{ dep }}</span> }
                            </div>
                          }
                        </div>
                        <div class="controls">
                          <div class="row"><mat-slide-toggle [(ngModel)]="s.enabled" (ngModelChange)="changed()">Enabled</mat-slide-toggle><mi-field-hint for="workflowStepEnabled"></mi-field-hint></div>
                          <mat-form-field appearance="outline" class="w200" subscriptSizing="dynamic">
                            <mat-label>On failure</mat-label>
                            <mat-select [(ngModel)]="s.onFail" (ngModelChange)="changed()" [disabled]="!s.enabled">
                              @for (p of policies; track p.value) { <mat-option [value]="p.value" [matTooltip]="p.hint">{{ p.label }}</mat-option> }
                            </mat-select>
                            <mi-field-hint matSuffix for="workflowOnFail"></mi-field-hint>
                          </mat-form-field>
                          @if (describe(s.id)?.params?.length) {
                            <mat-form-field appearance="outline" class="w200" subscriptSizing="dynamic">
                              <mat-label>Params JSON</mat-label>
                              <input matInput class="mono" [ngModel]="paramText(s)" (ngModelChange)="setParams(s, $event)" [disabled]="!s.enabled" [matTooltip]="paramHelp(s.id)">
                              <mi-field-hint matSuffix for="workflowParams"></mi-field-hint>
                            </mat-form-field>
                          }
                        </div>
                      </div>
                    }
                  </div>
                }
              </div>
            </mat-tab>

            <mat-tab label="Execution plan">
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

                  <h3>Stages <span class="muted">steps in the same stage run concurrently</span></h3>
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
                    <button mat-button type="button" (click)="view(h.version)">Load into editor</button>
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
    .tab { padding: 18px 0 4px; } .w200 { width: 200px; } .w300 { width: 300px; }
    .steps { display: flex; flex-direction: column; gap: 10px; }
    .step { display: grid; grid-template-columns: 56px minmax(0, 1fr) auto; gap: 14px; align-items: start; padding: 12px 14px; border: 1px solid var(--mi-border, #e0e3ea); border-radius: 10px; background: var(--mi-surface, #fff); }
    .step.disabled { opacity: .62; background: var(--mi-neutral-soft, #f3f4f7); }
    .step.blocked { border-color: var(--mi-bad); }
    .step.required { border-left: 4px solid var(--mi-primary, #3f51b5); }
    .handle { display: flex; flex-direction: column; align-items: center; gap: 2px; cursor: grab; color: var(--mi-text-2); padding-top: 4px; }
    .handle .num { font-size: 12px; font-weight: 700; }
    .title-row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 2px; }
    .deps { margin-top: 6px; display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
    .controls { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; justify-content: flex-end; }
    .cdk-drag-preview { box-shadow: 0 8px 24px rgba(0,0,0,.18); border-radius: 10px; background: #fff; }
    .cdk-drag-placeholder { opacity: .25; }
    .cdk-drag-animating, .steps.cdk-drop-list-dragging .step:not(.cdk-drag-placeholder) { transition: transform 200ms cubic-bezier(0, 0, 0.2, 1); }
    .stages { padding-left: 20px; display: flex; flex-direction: column; gap: 6px; } .stages li { display: flex; gap: 6px; flex-wrap: wrap; }
    .graph { white-space: pre; overflow: auto; max-height: 480px; padding: 12px; border-radius: 8px; background: var(--mi-neutral-soft, #f3f4f7); font-size: 12px; }
  `]
})
export class WorkflowsComponent {
  private readonly api = inject(SuiteApiService);
  readonly policies = POLICIES;
  readonly historyColumns = ['version', 'name', 'author', 'comment', 'steps', 'created', 'actions'];

  author = 'policy-admin';
  comment = '';

  readonly catalog = signal<Record<string, WorkflowStepDescriptor>>({});
  readonly draft = signal<WorkflowDefinition | null>(null);
  readonly source = signal('active');
  readonly validation = signal<{ valid: boolean; error?: string | null; plan?: WorkflowPlan | null } | null>(null);
  readonly history = signal<WorkflowVersion[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<{ ok: boolean; text: string } | null>(null);
  readonly enabledCount = computed(() => this.draft()?.steps.filter(s => s.enabled).length ?? 0);

  private readonly validate$ = new Subject<WorkflowDefinition>();

  constructor() {
    this.validate$.pipe(debounceTime(250), switchMap(d => this.api.validateWorkflow(d)), takeUntilDestroyed())
      .subscribe({ next: v => this.validation.set(v), error: e => this.validation.set({ valid: false, error: describeError(e) }) });
    this.load();
  }

  load(): void {
    this.loading.set(true); this.error.set(null); this.message.set(null);
    forkJoin({ catalog: this.api.workflowCatalog(), active: this.api.workflowActive(), history: this.api.workflowHistory() }).subscribe({
      next: r => {
        this.catalog.set(Object.fromEntries(r.catalog.map(c => [c.id, c])));
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

  private setDraft(def: WorkflowDefinition, source: string): void {
    this.draft.set(structuredClone(def));
    this.source.set(source);
    this.changed();
  }

  changed(): void {
    const d = this.draft(); if (!d) return;
    this.draft.set({ ...d, steps: [...d.steps] });
    this.validate$.next(d);
  }

  describe(id: string): WorkflowStepDescriptor | undefined { return this.catalog()[id]; }

  isOn(id: string): boolean { return this.draft()?.steps.find(s => s.id === id)?.enabled ?? false; }

  /** True when an enabled step has a dependency that is disabled or listed after it. */
  blocked(id: string): boolean {
    const d = this.draft(); const desc = this.describe(id); if (!d || !desc) return false;
    const me = d.steps.find(s => s.id === id); if (!me?.enabled) return false;
    const pos = (x: string) => d.steps.findIndex(s => s.id === x);
    return (me.dependsOn ?? desc.dependsOn).some(dep => !this.isOn(dep) || pos(dep) > pos(id));
  }

  stageOf(id: string): number | null {
    const st = this.validation()?.plan?.stages.find(s => s.steps.includes(id));
    return st ? st.index : null;
  }

  drop(e: CdkDragDrop<WorkflowStepConfig[]>): void {
    const d = this.draft(); if (!d) return;
    moveItemInArray(d.steps, e.previousIndex, e.currentIndex);
    this.draft.set({ ...d, steps: [...d.steps] });
    this.changed();
  }

  paramText(s: WorkflowStepConfig): string { return s.params && Object.keys(s.params).length ? JSON.stringify(s.params) : ''; }
  paramHelp(id: string): string { return (this.describe(id)?.params ?? []).map(p => `${p.name} (${p.type}, default ${p.default}): ${p.description}`).join('\n'); }
  setParams(s: WorkflowStepConfig, text: string): void {
    if (!text.trim()) { s.params = null; this.changed(); return; }
    try { s.params = JSON.parse(text) as Record<string, unknown>; this.changed(); }
    catch { this.validation.set({ valid: false, error: `Params for '${s.id}' are not valid JSON.` }); }
  }

  publish(): void {
    const d = this.draft(); if (!d) return;
    this.api.publishWorkflow(d, this.author, this.comment || undefined).subscribe({
      next: v => { this.message.set({ ok: true, text: `Published and activated v${v.version} · ${v.enabledSteps}/${v.totalSteps} steps enabled` }); this.comment = ''; this.load(); },
      error: e => this.message.set({ ok: false, text: describeError(e) })
    });
  }

  view(version: number): void {
    this.api.workflowVersion(version).subscribe({
      next: d => { this.setDraft(d, `v${version} (not active)`); this.message.set({ ok: true, text: `Loaded v${version} into the editor — publish to activate it` }); },
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
