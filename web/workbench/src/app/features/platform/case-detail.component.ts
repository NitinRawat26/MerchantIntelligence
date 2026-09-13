import { Component, effect, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTabsModule } from '@angular/material/tabs';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { AuditEvent, CaseNote, CaseStatus, MerchantCase } from '../../shared/models';
import { JsonViewComponent, StatusComponent, outcomeClass } from '../../shared/ui';
import { CASE_STATUSES } from './cases.component';

@Component({
  selector: 'mi-case-detail',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, DatePipe, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTabsModule, JsonViewComponent, StatusComponent],
  styleUrl: './platform.scss',
  template: `
    <div class="page">
      <a mat-button routerLink="/cases"><mat-icon>arrow_back</mat-icon> Back to queue</a>
      <mi-status [loading]="loading()" [error]="error()"></mi-status>

      @if (case(); as c) {
        <mat-card appearance="outlined" class="result-card" [class]="outcomeClass(c.finalDecision ?? c.status)">
          <mat-card-content class="summary">
            <div class="summary-text">
              <div class="headline">
                <h2 class="title">{{ c.merchantName }}</h2>
                <span class="pill" [class]="outcomeClass(c.status)">{{ c.status }}</span>
                <span class="pill neutral">{{ c.priority }} priority</span>
                @if (c.rulesOutcome) { <span class="pill" [class]="outcomeClass(c.rulesOutcome)">Rules: {{ c.rulesOutcome }}</span> }
                @if (c.finalDecision) { <span class="pill" [class]="outcomeClass(c.finalDecision)">Final: {{ c.finalDecision }}</span> }
              </div>
              <dl class="kv">
                <dt>Case ID</dt><dd><code>{{ c.id }}</code></dd>
                <dt>External ref</dt><dd>{{ c.externalRef || '—' }}</dd>
                <dt>Risk score</dt><dd>{{ c.riskScore ?? '—' }} {{ c.riskTier ? '(' + c.riskTier + ')' : '' }}</dd>
                <dt>Assigned to</dt><dd>{{ c.assignedTo || 'unassigned' }}</dd>
                <dt>Created</dt><dd>{{ c.createdAt | date:'medium' }}</dd>
                <dt>Updated</dt><dd>{{ c.updatedAt | date:'medium' }}</dd>
              </dl>
            </div>
          </mat-card-content>
        </mat-card>

        @if (actionError(); as e) { <mi-status [error]="e"></mi-status> }

        <div class="two-col">
          <mat-card appearance="outlined">
            <mat-card-header><mat-card-title>Actions</mat-card-title><mat-card-subtitle>Every action is written to the hash-chained audit log</mat-card-subtitle></mat-card-header>
            <mat-card-content>
              <form [formGroup]="actor" class="row">
                <mat-form-field appearance="outline"><mat-label>Acting as</mat-label><input matInput formControlName="actor" required></mat-form-field>
              </form>

              <h4>Assign</h4>
              <form [formGroup]="assign" class="row" (ngSubmit)="doAssign()">
                <mat-form-field appearance="outline"><mat-label>Assignee</mat-label><input matInput formControlName="assignee" required></mat-form-field>
                <button mat-stroked-button type="submit" [disabled]="assign.invalid || terminal()">Assign</button>
              </form>

              <h4>Change status</h4>
              <form [formGroup]="status" class="row" (ngSubmit)="doStatus()">
                <mat-form-field appearance="outline"><mat-label>Status</mat-label>
                  <mat-select formControlName="status">@for (s of workingStatuses; track s) {<mat-option [value]="s">{{ s }}</mat-option>}</mat-select></mat-form-field>
                <mat-form-field appearance="outline"><mat-label>Reason</mat-label><input matInput formControlName="reason"></mat-form-field>
                <button mat-stroked-button type="submit" [disabled]="terminal()">Update</button>
              </form>

              <h4>Final decision</h4>
              <p class="muted">Overriding the rules outcome requires a reason.</p>
              <form [formGroup]="decide" class="row" (ngSubmit)="doDecide()">
                <mat-form-field appearance="outline"><mat-label>Reason</mat-label><input matInput formControlName="reason"></mat-form-field>
                <button mat-flat-button color="primary" type="button" (click)="doDecide('Approved')" [disabled]="terminal()"><mat-icon>check</mat-icon> Approve</button>
                <button mat-flat-button color="warn" type="button" (click)="doDecide('Declined')" [disabled]="terminal()"><mat-icon>close</mat-icon> Decline</button>
              </form>
              @if (terminal()) { <p class="muted">Case is closed ({{ c.status }}); no further changes allowed.</p> }
            </mat-card-content>
          </mat-card>

          <mat-card appearance="outlined">
            <mat-card-header><mat-card-title>Notes</mat-card-title></mat-card-header>
            <mat-card-content>
              <form [formGroup]="note" (ngSubmit)="doNote()">
                <mat-form-field appearance="outline" class="full"><mat-label>Add note</mat-label><textarea matInput rows="3" formControlName="body"></textarea></mat-form-field>
                <button mat-stroked-button type="submit" [disabled]="note.invalid">Add note</button>
              </form>
              <ul class="timeline">
                @for (n of notes(); track n.id) {
                  <li><span class="when">{{ n.createdAt | date:'short' }}<br><strong>{{ n.author }}</strong></span><span>{{ n.body }}</span></li>
                } @empty { <li><span class="muted">No notes yet.</span></li> }
              </ul>
            </mat-card-content>
          </mat-card>
        </div>

        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>Case audit trail</mat-card-title><mat-card-subtitle>{{ audit().length }} events · SHA-256 chained</mat-card-subtitle></mat-card-header>
          <mat-card-content>
            <ul class="timeline">
              @for (a of audit(); track a.seq) {
                <li>
                  <span class="when">#{{ a.seq }} · {{ a.occurredAt | date:'short' }}<br><strong>{{ a.actor }}</strong></span>
                  <span><code>{{ a.action }}</code> <span class="muted">{{ detail(a) }}</span><br><span class="hash">{{ a.hash }}</span></span>
                </li>
              }
            </ul>
          </mat-card-content>
        </mat-card>

        @if (c.snapshot) { <mi-json [data]="c.snapshot" title="Scoring snapshot at case creation"></mi-json> }
      }
    </div>
  `,
  styles: [`.title { margin: 0; font-size: 22px; font-weight: 500; }`]
})
export class CaseDetailComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  readonly outcomeClass = outcomeClass;
  readonly id = input.required<string>();

  readonly workingStatuses: CaseStatus[] = CASE_STATUSES.filter(s => s === 'Open' || s === 'InReview' || s === 'PendingDocuments' || s === 'Withdrawn');
  readonly actor = this.fb.nonNullable.group({ actor: ['analyst', Validators.required] });
  readonly assign = this.fb.nonNullable.group({ assignee: ['', Validators.required] });
  readonly status = this.fb.nonNullable.group({ status: ['InReview' as CaseStatus], reason: [''] });
  readonly decide = this.fb.nonNullable.group({ reason: [''] });
  readonly note = this.fb.nonNullable.group({ body: ['', Validators.required] });

  readonly case = signal<MerchantCase | null>(null);
  readonly notes = signal<CaseNote[]>([]);
  readonly audit = signal<AuditEvent[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly terminal = signal(false);

  constructor() { effect(() => this.load(this.id()), { allowSignalWrites: true }); }

  load(id: string): void {
    this.loading.set(true); this.error.set(null);
    this.api.case(id).subscribe({
      next: c => { this.apply(c); this.loading.set(false); },
      error: e => { this.error.set(describeError(e)); this.loading.set(false); }
    });
    this.api.notes(id).subscribe({ next: n => this.notes.set(n), error: () => {} });
    this.api.caseAudit(id).subscribe({ next: a => this.audit.set(a), error: () => {} });
  }

  private apply(c: MerchantCase): void {
    this.case.set(c);
    this.terminal.set(c.status === 'Approved' || c.status === 'Declined' || c.status === 'Withdrawn');
    this.api.caseAudit(c.id).subscribe({ next: a => this.audit.set(a), error: () => {} });
  }

  private run(obs: ReturnType<SuiteApiService['assignCase']>): void {
    this.actionError.set(null);
    obs.subscribe({ next: c => this.apply(c), error: e => this.actionError.set(describeError(e)) });
  }

  doAssign(): void { this.run(this.api.assignCase(this.id(), this.assign.controls.assignee.value, this.actor.controls.actor.value)); }
  doStatus(): void { const v = this.status.getRawValue(); this.run(this.api.setCaseStatus(this.id(), v.status, this.actor.controls.actor.value, v.reason || undefined)); }
  doDecide(decision?: 'Approved' | 'Declined'): void {
    if (!decision) return;
    this.run(this.api.decideCase(this.id(), decision, this.actor.controls.actor.value, this.decide.controls.reason.value));
  }
  doNote(): void {
    this.actionError.set(null);
    this.api.addNote(this.id(), this.actor.controls.actor.value, this.note.controls.body.value).subscribe({
      next: n => { this.notes.update(list => [n, ...list]); this.note.reset({ body: '' }); this.api.caseAudit(this.id()).subscribe(a => this.audit.set(a)); },
      error: e => this.actionError.set(describeError(e))
    });
  }

  detail(a: AuditEvent): string {
    if (a.detail == null) return '';
    return typeof a.detail === 'string' ? a.detail : JSON.stringify(a.detail);
  }
}
