import { Component, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { CasePriority, CaseQueueStats, CaseStatus, MerchantCase } from '../../shared/models';
import { StatusComponent, outcomeClass } from '../../shared/ui';

export const CASE_STATUSES: CaseStatus[] = ['Open', 'InReview', 'PendingDocuments', 'Approved', 'Declined', 'Withdrawn'];
export const CASE_PRIORITIES: CasePriority[] = ['Low', 'Normal', 'High', 'Urgent'];

@Component({
  selector: 'mi-cases',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, DecimalPipe, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTableModule, StatusComponent],
  styleUrl: './platform.scss',
  template: `
    <div class="page">
      @if (stats(); as s) {
        <div class="stat-row">
          <div class="stat"><div class="stat-value">{{ s.open }}</div><div class="stat-label">Open</div></div>
          <div class="stat"><div class="stat-value">{{ s.inReview }}</div><div class="stat-label">In review</div></div>
          <div class="stat"><div class="stat-value">{{ s.pendingDocuments }}</div><div class="stat-label">Pending docs</div></div>
          <div class="stat"><div class="stat-value">{{ s.approved }}</div><div class="stat-label">Approved</div></div>
          <div class="stat"><div class="stat-value">{{ s.declined }}</div><div class="stat-label">Declined</div></div>
          <div class="stat"><div class="stat-value">{{ s.overrides }}</div><div class="stat-label">Overrides</div></div>
          <div class="stat"><div class="stat-value">{{ s.averageOpenAgeHours | number:'1.0-1' }}h</div><div class="stat-label">Avg open age</div></div>
        </div>
      }

      <mat-card appearance="outlined">
        <mat-card-header>
          <mat-icon mat-card-avatar>inbox</mat-icon>
          <mat-card-title>Case queue</mat-card-title>
          <mat-card-subtitle>Analyst review queue · click a row to open</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <form [formGroup]="filter" class="row" (ngSubmit)="load()">
            <mat-form-field appearance="outline" class="w200"><mat-label>Status</mat-label>
              <mat-select formControlName="status"><mat-option value="">Any</mat-option>@for (s of statuses; track s) {<mat-option [value]="s">{{ s }}</mat-option>}</mat-select></mat-form-field>
            <mat-form-field appearance="outline" class="w200"><mat-label>Assigned to</mat-label><input matInput formControlName="assignedTo"></mat-form-field>
            <button mat-stroked-button type="submit"><mat-icon>refresh</mat-icon> Refresh</button>
          </form>
          <mi-status [loading]="loading()" [error]="error()"></mi-status>
          @if (cases().length) {
            <table mat-table [dataSource]="cases()" class="full">
              <ng-container matColumnDef="merchant"><th mat-header-cell *matHeaderCellDef>Merchant</th><td mat-cell *matCellDef="let c"><strong>{{ c.merchantName }}</strong><br><span class="muted">{{ c.externalRef || c.id }}</span></td></ng-container>
              <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Status</th><td mat-cell *matCellDef="let c"><span class="pill small" [class]="outcomeClass(c.status)">{{ c.status }}</span></td></ng-container>
              <ng-container matColumnDef="priority"><th mat-header-cell *matHeaderCellDef>Priority</th><td mat-cell *matCellDef="let c">{{ c.priority }}</td></ng-container>
              <ng-container matColumnDef="score"><th mat-header-cell *matHeaderCellDef>Score</th><td mat-cell *matCellDef="let c">{{ c.riskScore ?? '—' }} <span class="muted">{{ c.riskTier }}</span></td></ng-container>
              <ng-container matColumnDef="rules"><th mat-header-cell *matHeaderCellDef>Rules</th><td mat-cell *matCellDef="let c">@if (c.rulesOutcome) {<span class="pill small" [class]="outcomeClass(c.rulesOutcome)">{{ c.rulesOutcome }}</span>}</td></ng-container>
              <ng-container matColumnDef="assignee"><th mat-header-cell *matHeaderCellDef>Assignee</th><td mat-cell *matCellDef="let c">{{ c.assignedTo || '—' }}</td></ng-container>
              <ng-container matColumnDef="updated"><th mat-header-cell *matHeaderCellDef>Updated</th><td mat-cell *matCellDef="let c">{{ c.updatedAt | date:'short' }}</td></ng-container>
              <tr mat-header-row *matHeaderRowDef="columns"></tr>
              <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="open(row)"></tr>
            </table>
          } @else if (!loading()) { <p class="empty">No cases match. Score a merchant with “open a review case”, or create one below.</p> }
        </mat-card-content>
      </mat-card>

      <mat-card appearance="outlined">
        <mat-card-header><mat-card-title>Create case manually</mat-card-title></mat-card-header>
        <mat-card-content>
          <form [formGroup]="create" (ngSubmit)="createCase()" class="grid-4">
            <mat-form-field appearance="outline"><mat-label>Merchant name</mat-label><input matInput formControlName="merchantName" required></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>External ref</mat-label><input matInput formControlName="externalRef"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Priority</mat-label>
              <mat-select formControlName="priority">@for (p of priorities; track p) {<mat-option [value]="p">{{ p }}</mat-option>}</mat-select></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Actor</mat-label><input matInput formControlName="actor" required></mat-form-field>
            <div class="actions wide"><button mat-flat-button color="primary" type="submit" [disabled]="create.invalid">Create case</button>
              @if (createError(); as e) { <span class="text-high">{{ e }}</span> }</div>
          </form>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`.w200 { width: 220px; }`]
})
export class CasesComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  private readonly router = inject(Router);
  readonly outcomeClass = outcomeClass;
  readonly statuses = CASE_STATUSES;
  readonly priorities = CASE_PRIORITIES;
  readonly columns = ['merchant', 'status', 'priority', 'score', 'rules', 'assignee', 'updated'];

  readonly filter = this.fb.nonNullable.group({ status: [''], assignedTo: [''] });
  readonly create = this.fb.nonNullable.group({ merchantName: ['', Validators.required], externalRef: [''], priority: ['Normal' as CasePriority], actor: ['analyst', Validators.required] });

  readonly cases = signal<MerchantCase[]>([]);
  readonly stats = signal<CaseQueueStats | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly createError = signal<string | null>(null);

  constructor() { this.load(); }

  load(): void {
    const f = this.filter.getRawValue();
    this.loading.set(true); this.error.set(null);
    this.api.cases((f.status || null) as CaseStatus | null, f.assignedTo || null).subscribe({
      next: c => { this.cases.set(c); this.loading.set(false); },
      error: e => { this.error.set(describeError(e)); this.loading.set(false); }
    });
    this.api.caseStats().subscribe({ next: s => this.stats.set(s), error: () => {} });
  }

  open(c: MerchantCase): void { this.router.navigate(['/cases', c.id]); }

  createCase(): void {
    const v = this.create.getRawValue();
    this.createError.set(null);
    this.api.createCase({ merchantName: v.merchantName, externalRef: v.externalRef || undefined, priority: v.priority, actor: v.actor }).subscribe({
      next: c => this.open(c),
      error: e => this.createError.set(describeError(e))
    });
  }
}
