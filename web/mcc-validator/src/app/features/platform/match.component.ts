import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { MatchResult } from '../../shared/models';
import { JsonViewComponent, StatusComponent } from '../../shared/ui';

@Component({
  selector: 'mi-match',
  standalone: true,
  imports: [ReactiveFormsModule, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatTableModule, JsonViewComponent, StatusComponent],
  styleUrl: './platform.scss',
  template: `
    <div class="page">
      <mat-card appearance="outlined">
        <mat-card-header>
          <mat-icon mat-card-avatar>policy</mat-icon>
          <mat-card-title>Terminated-merchant (MATCH) inquiry</mat-card-title>
          <mat-card-subtitle>Uses the configured provider: local terminated-merchant CSV, a MATCH-compatible HTTP endpoint, or reports "not configured" — never a false clear</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <form [formGroup]="form" (ngSubmit)="submit()">
            <div class="grid-4">
              <mat-form-field appearance="outline"><mat-label>Legal name</mat-label><input matInput formControlName="legalName" required></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>DBA</mat-label><input matInput formControlName="doingBusinessAs"></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Tax ID</mat-label><input matInput formControlName="taxId"></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Country</mat-label><input matInput formControlName="country" maxlength="2"></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Address</mat-label><input matInput formControlName="addressLine"></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>City</mat-label><input matInput formControlName="city"></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Region</mat-label><input matInput formControlName="region"></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Postal code</mat-label><input matInput formControlName="postalCode"></mat-form-field>
            </div>
            <h3>Principal</h3>
            <div class="grid-4">
              <mat-form-field appearance="outline"><mat-label>First name</mat-label><input matInput formControlName="firstName"></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Last name</mat-label><input matInput formControlName="lastName"></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Date of birth</mat-label><input matInput type="date" formControlName="dateOfBirth"></mat-form-field>
            </div>
            <div class="actions"><button mat-flat-button color="primary" type="submit" [disabled]="form.invalid || loading()"><mat-icon>search</mat-icon> Run inquiry</button></div>
          </form>
        </mat-card-content>
      </mat-card>

      <mi-status [loading]="loading()" [error]="error()"></mi-status>

      @if (result(); as r) {
        <mat-card appearance="outlined" class="result-card" [class]="cls(r)">
          <mat-card-content class="summary">
            <div class="summary-text">
              <div class="headline">
                <span class="pill" [class]="cls(r)">{{ label(r) }}</span>
                <span class="pill neutral">{{ r.availability }}</span>
                <span class="muted">provider: {{ r.provider }}</span>
              </div>
              @if (r.message) { <p>{{ r.message }}</p> }
              @if (r.availability === 'NotConfigured') {
                <p class="muted">Set <code>Match:LocalListPath</code> to a terminated-merchant CSV, or <code>Match:Endpoint</code> + <code>Match:ApiKey</code> for a MATCH-compatible service, to get real results. Until then the unified score treats MATCH as a coverage gap, not a clear.</p>
              }
              @if (r.hits.length) {
                <table mat-table [dataSource]="r.hits" class="full">
                  <ng-container matColumnDef="matchedOn"><th mat-header-cell *matHeaderCellDef>Matched on</th><td mat-cell *matCellDef="let h">{{ h.matchedOn }}</td></ng-container>
                  <ng-container matColumnDef="reason"><th mat-header-cell *matHeaderCellDef>Reason</th><td mat-cell *matCellDef="let h"><code>{{ h.reasonCode }}</code> {{ h.reasonDescription }}</td></ng-container>
                  <ng-container matColumnDef="terminated"><th mat-header-cell *matHeaderCellDef>Terminated</th><td mat-cell *matCellDef="let h">{{ h.terminationDate ?? '—' }}</td></ng-container>
                  <ng-container matColumnDef="acquirer"><th mat-header-cell *matHeaderCellDef>Acquirer</th><td mat-cell *matCellDef="let h">{{ h.acquirer ?? '—' }}</td></ng-container>
                  <tr mat-header-row *matHeaderRowDef="columns"></tr>
                  <tr mat-row *matRowDef="let row; columns: columns"></tr>
                </table>
              }
            </div>
          </mat-card-content>
        </mat-card>
        <mi-json [data]="r"></mi-json>
      }
    </div>
  `
})
export class MatchComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  readonly columns = ['matchedOn', 'reason', 'terminated', 'acquirer'];
  readonly form = this.fb.nonNullable.group({
    legalName: ['Acme Widgets LLC', Validators.required], doingBusinessAs: [''], taxId: [''], country: ['US'],
    addressLine: [''], city: [''], region: [''], postalCode: [''], firstName: [''], lastName: [''], dateOfBirth: ['']
  });
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly result = signal<MatchResult | null>(null);

  submit(): void {
    const v = this.form.getRawValue();
    const opt = (s: string) => s || undefined;
    this.loading.set(true); this.error.set(null); this.result.set(null);
    this.api.matchInquiry({
      legalName: v.legalName, doingBusinessAs: opt(v.doingBusinessAs), taxId: opt(v.taxId), country: opt(v.country),
      addressLine: opt(v.addressLine), city: opt(v.city), region: opt(v.region), postalCode: opt(v.postalCode),
      principals: v.firstName || v.lastName ? [{ firstName: v.firstName, lastName: v.lastName, dateOfBirth: opt(v.dateOfBirth) }] : []
    }).subscribe({ next: r => { this.result.set(r); this.loading.set(false); }, error: e => { this.error.set(describeError(e)); this.loading.set(false); } });
  }

  label(r: MatchResult): string { return r.found === true ? 'LISTED' : r.found === false ? 'No record' : 'Unknown – not checked'; }
  cls(r: MatchResult): string { return r.found === true ? 'bad' : r.found === false ? 'good' : 'warn'; }
}
