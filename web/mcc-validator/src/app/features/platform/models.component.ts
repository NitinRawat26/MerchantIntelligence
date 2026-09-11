import { Component, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { ChampionChallengerReport, Decision, DriftReport, LoggedDecision, ModelsResponse } from '../../shared/models';
import { JsonViewComponent, StatusComponent, outcomeClass } from '../../shared/ui';

@Component({
  selector: 'mi-models',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, DecimalPipe, PercentPipe, MatButtonModule, MatCardModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTableModule, MatTabsModule, JsonViewComponent, StatusComponent],
  styleUrl: './platform.scss',
  template: `
    <div class="page">
      <mat-card appearance="outlined">
        <mat-card-header>
          <mat-icon mat-card-avatar>model_training</mat-icon>
          <mat-card-title>Model operations</mat-card-title>
          <mat-card-subtitle>Registry · champion/challenger · PSI drift · retraining on labelled decisions</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <mi-status [loading]="loading()" [error]="error()"></mi-status>
          @if (message(); as m) { <p [class]="m.ok ? 'text-low' : 'text-high'">{{ m.text }}</p> }
          <mat-tab-group>
            <mat-tab label="Registry">
              <div class="tab">
                @if (models(); as m) {
                  <div class="headline"><span class="pill good">Champion {{ m.champion }}</span> <span class="pill" [class]="m.challenger ? 'warn' : 'neutral'">Challenger {{ m.challenger ?? 'none' }}</span></div>
                  <table mat-table [dataSource]="m.registry" class="full">
                    <ng-container matColumnDef="version"><th mat-header-cell *matHeaderCellDef>Version</th><td mat-cell *matCellDef="let r"><code>{{ r.version }}</code></td></ng-container>
                    <ng-container matColumnDef="role"><th mat-header-cell *matHeaderCellDef>Role</th><td mat-cell *matCellDef="let r"><span class="pill small" [class]="outcomeClass(r.role)">{{ r.role }}</span></td></ng-container>
                    <ng-container matColumnDef="rows"><th mat-header-cell *matHeaderCellDef>Training rows</th><td mat-cell *matCellDef="let r">{{ r.trainingRows ?? '—' }}</td></ng-container>
                    <ng-container matColumnDef="metrics"><th mat-header-cell *matHeaderCellDef>Metrics</th><td mat-cell *matCellDef="let r" class="detail">{{ metrics(r.metrics) }}</td></ng-container>
                    <ng-container matColumnDef="registered"><th mat-header-cell *matHeaderCellDef>Registered</th><td mat-cell *matCellDef="let r">{{ r.registeredAt | date:'short' }}</td></ng-container>
                    <tr mat-header-row *matHeaderRowDef="registryColumns"></tr>
                    <tr mat-row *matRowDef="let row; columns: registryColumns"></tr>
                  </table>
                }
                <h4>Retrain</h4>
                <form [formGroup]="retrainForm" class="row" (ngSubmit)="retrain()">
                  <mat-form-field appearance="outline" class="w200"><mat-label>Actor</mat-label><input matInput formControlName="actor" required></mat-form-field>
                  <mat-form-field appearance="outline" class="w200"><mat-label>Synthetic rows</mat-label><input matInput type="number" formControlName="syntheticRows" min="0" max="200000"></mat-form-field>
                  <mat-checkbox formControlName="registerAsChallenger">Register as challenger</mat-checkbox>
                  <button mat-flat-button color="primary" type="submit" [disabled]="retrainForm.invalid || busy()"><mat-icon>autorenew</mat-icon> Retrain</button>
                </form>
                <h4>Promote challenger → champion</h4>
                <form [formGroup]="promoteForm" class="row" (ngSubmit)="promote()">
                  <mat-form-field appearance="outline" class="w300"><mat-label>Justification</mat-label><input matInput formControlName="justification"></mat-form-field>
                  <button mat-stroked-button color="primary" type="submit" [disabled]="!models()?.challenger || busy()"><mat-icon>upgrade</mat-icon> Promote</button>
                </form>
              </div>
            </mat-tab>

            <mat-tab label="Champion vs challenger">
              <div class="tab">
                <button mat-stroked-button type="button" (click)="loadCompare()"><mat-icon>compare</mat-icon> Refresh comparison</button>
                @if (compare(); as c) {
                  <p><strong>Recommendation:</strong> {{ c.recommendation }} · {{ c.disagreements }} disagreements</p>
                  <div class="two-col">
                    @for (p of [c.champion, c.challenger]; track $index) {
                      @if (p) {
                        <div>
                          <h4>{{ $index === 0 ? 'Champion' : 'Challenger' }} <code>{{ p.version }}</code></h4>
                          <dl class="kv">
                            <dt>Scored</dt><dd>{{ p.scored }}</dd>
                            <dt>With outcome</dt><dd>{{ p.withOutcome }}</dd>
                            <dt>Accuracy</dt><dd>{{ p.accuracy != null ? (p.accuracy | percent:'1.0-1') : '—' }}</dd>
                            <dt>Approval precision</dt><dd>{{ p.approvalPrecision != null ? (p.approvalPrecision | percent:'1.0-1') : '—' }}</dd>
                            <dt>Decline recall</dt><dd>{{ p.declineRecall != null ? (p.declineRecall | percent:'1.0-1') : '—' }}</dd>
                          </dl>
                        </div>
                      }
                    }
                  </div>
                }
              </div>
            </mat-tab>

            <mat-tab label="Drift">
              <div class="tab">
                <button mat-stroked-button type="button" (click)="loadDrift()"><mat-icon>trending_up</mat-icon> Refresh drift</button>
                @if (drift(); as d) {
                  <div class="headline"><span class="pill" [class]="outcomeClass(d.overallStatus)">{{ d.overallStatus }}</span>
                    <span class="muted">{{ d.referenceRows }} reference · {{ d.recentRows }} recent rows · prediction PSI {{ d.predictionDriftPsi | number:'1.3-3' }}</span></div>
                  @for (a of d.alerts; track a) { <p class="text-high">{{ a }}</p> }
                  <div class="bar-list">
                    @for (f of d.features; track f.feature) {
                      <div class="bar-row"><span>{{ f.feature }} <span class="pill small" [class]="outcomeClass(f.status)">{{ f.status }}</span></span>
                        <div class="bar"><span class="pos" [style.left.%]="0" [style.width.%]="min100(f.psi * 200)"></span></div><span>PSI {{ f.psi | number:'1.3-3' }}</span></div>
                    }
                  </div>
                  @if (!d.features.length) { <p class="muted">Not enough logged decisions yet — score merchants to populate the decision log.</p> }
                }
              </div>
            </mat-tab>

            <mat-tab label="Decision log">
              <div class="tab">
                <div class="row">
                  <button mat-stroked-button type="button" (click)="loadDecisions()"><mat-icon>refresh</mat-icon> Refresh</button>
                  <mat-form-field appearance="outline" class="w200"><mat-label>Label as actor</mat-label><input matInput [formControl]="labelActor"></mat-form-field>
                  <span class="muted">Record the real outcome to feed champion/challenger metrics and retraining.</span>
                </div>
                <table mat-table [dataSource]="decisions()" class="full">
                  <ng-container matColumnDef="id"><th mat-header-cell *matHeaderCellDef>#</th><td mat-cell *matCellDef="let d">{{ d.id }}</td></ng-container>
                  <ng-container matColumnDef="app"><th mat-header-cell *matHeaderCellDef>Application</th><td mat-cell *matCellDef="let d" class="detail">MCC {{ d.application.merchantCategoryCode }} · {{ d.application.annualVolume | number:'1.0-0' }} vol · {{ d.application.averageTicket | number:'1.0-0' }} avg</td></ng-container>
                  <ng-container matColumnDef="predicted"><th mat-header-cell *matHeaderCellDef>Champion</th><td mat-cell *matCellDef="let d"><span class="pill small" [class]="outcomeClass(d.predicted)">{{ d.predicted }}</span> {{ d.confidence | percent:'1.0-0' }}</td></ng-container>
                  <ng-container matColumnDef="challenger"><th mat-header-cell *matHeaderCellDef>Challenger</th><td mat-cell *matCellDef="let d">@if (d.challengerPredicted) {<span class="pill small" [class]="outcomeClass(d.challengerPredicted)">{{ d.challengerPredicted }}</span>} @else {—}</td></ng-container>
                  <ng-container matColumnDef="actual"><th mat-header-cell *matHeaderCellDef>Actual</th><td mat-cell *matCellDef="let d">
                    @if (d.actual) { <span class="pill small" [class]="outcomeClass(d.actual)">{{ d.actual }}</span> }
                    @else { @for (o of outcomes; track o) { <button mat-button type="button" (click)="label(d, o)">{{ o }}</button> } }
                  </td></ng-container>
                  <ng-container matColumnDef="when"><th mat-header-cell *matHeaderCellDef>Scored</th><td mat-cell *matCellDef="let d">{{ d.scoredAt | date:'short' }}</td></ng-container>
                  <tr mat-header-row *matHeaderRowDef="decisionColumns"></tr>
                  <tr mat-row *matRowDef="let row; columns: decisionColumns"></tr>
                </table>
                @if (!decisions().length) { <p class="empty">No decisions logged yet.</p> }
              </div>
            </mat-tab>
          </mat-tab-group>
          @if (lastRetrain(); as r) { <mi-json [data]="r" title="Last retrain result" [expanded]="true"></mi-json> }
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`.tab { padding: 18px 0 4px; } .w200 { width: 200px; } .w300 { width: 320px; }`]
})
export class ModelsComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  readonly outcomeClass = outcomeClass;
  readonly registryColumns = ['version', 'role', 'rows', 'metrics', 'registered'];
  readonly decisionColumns = ['id', 'app', 'predicted', 'challenger', 'actual', 'when'];
  readonly outcomes: Decision[] = ['Approved', 'Declined', 'Cancelled'];

  readonly retrainForm = this.fb.nonNullable.group({ actor: ['ml-ops', Validators.required], syntheticRows: [5000, [Validators.min(0), Validators.max(200000)]], registerAsChallenger: [true] });
  readonly promoteForm = this.fb.nonNullable.group({ justification: [''] });
  readonly labelActor = this.fb.nonNullable.control('analyst');

  readonly models = signal<ModelsResponse | null>(null);
  readonly compare = signal<ChampionChallengerReport | null>(null);
  readonly drift = signal<DriftReport | null>(null);
  readonly decisions = signal<LoggedDecision[]>([]);
  readonly lastRetrain = signal<unknown>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<{ ok: boolean; text: string } | null>(null);

  constructor() { this.loadModels(); this.loadCompare(); this.loadDrift(); this.loadDecisions(); }

  loadModels(): void {
    this.loading.set(true); this.error.set(null);
    this.api.models().subscribe({ next: m => { this.models.set(m); this.loading.set(false); }, error: e => { this.error.set(describeError(e)); this.loading.set(false); } });
  }
  loadCompare(): void { this.api.compare().subscribe({ next: c => this.compare.set(c), error: e => this.error.set(describeError(e)) }); }
  loadDrift(): void { this.api.drift().subscribe({ next: d => this.drift.set(d), error: e => this.error.set(describeError(e)) }); }
  loadDecisions(): void { this.api.decisions(false, 100).subscribe({ next: d => this.decisions.set(d), error: e => this.error.set(describeError(e)) }); }

  retrain(): void {
    const v = this.retrainForm.getRawValue();
    this.busy.set(true); this.message.set(null);
    this.api.retrain(v.actor, v.syntheticRows, v.registerAsChallenger).subscribe({
      next: r => { this.lastRetrain.set(r); this.message.set({ ok: true, text: `Trained ${r.version} on ${r.labelledRows} labelled + ${r.syntheticRows} synthetic rows` }); this.busy.set(false); this.loadModels(); this.loadCompare(); },
      error: e => { this.message.set({ ok: false, text: describeError(e) }); this.busy.set(false); }
    });
  }
  promote(): void {
    this.busy.set(true); this.message.set(null);
    this.api.promote(this.retrainForm.controls.actor.value, this.promoteForm.controls.justification.value || undefined).subscribe({
      next: r => { this.message.set({ ok: true, text: `Promoted ${r.version} to champion` }); this.busy.set(false); this.loadModels(); this.loadCompare(); },
      error: e => { this.message.set({ ok: false, text: describeError(e) }); this.busy.set(false); }
    });
  }
  label(d: LoggedDecision, actual: Decision): void {
    this.api.recordOutcome(d.id, actual, this.labelActor.value).subscribe({
      next: () => { this.decisions.update(list => list.map(x => x.id === d.id ? { ...x, actual } : x)); this.loadCompare(); },
      error: e => this.message.set({ ok: false, text: describeError(e) })
    });
  }
  metrics(m: Record<string, unknown> | null | undefined): string {
    return m ? Object.entries(m).filter(([, v]) => typeof v === 'number').map(([k, v]) => `${k}=${(v as number).toFixed(3)}`).join('  ') : '—';
  }
  min100(v: number): number { return Math.min(100, v); }
}
