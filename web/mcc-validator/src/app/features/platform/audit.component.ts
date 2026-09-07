import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { AuditEvent, AuditVerification } from '../../shared/models';
import { StatusComponent } from '../../shared/ui';

@Component({
  selector: 'mi-audit',
  standalone: true,
  imports: [DatePipe, RouterLink, MatButtonModule, MatCardModule, MatIconModule, StatusComponent],
  styleUrl: './platform.scss',
  template: `
    <div class="page">
      <mat-card appearance="outlined" class="result-card" [class]="verification() ? (verification()!.valid ? 'good' : 'bad') : ''">
        <mat-card-header>
          <mat-icon mat-card-avatar>history</mat-icon>
          <mat-card-title>Audit trail</mat-card-title>
          <mat-card-subtitle>Append-only, SHA-256 hash-chained log of every case, rules and model action</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <div class="row">
            <button mat-flat-button color="primary" type="button" (click)="verify()"><mat-icon>verified</mat-icon> Verify chain integrity</button>
            <button mat-stroked-button type="button" (click)="load()"><mat-icon>refresh</mat-icon> Reload</button>
            @if (verification(); as v) {
              <span class="pill" [class]="v.valid ? 'good' : 'bad'">{{ v.valid ? 'Chain intact' : 'CHAIN BROKEN' }} · {{ v.eventsChecked }} events checked</span>
              @if (v.firstBrokenSeq != null) { <span class="text-high">first broken seq #{{ v.firstBrokenSeq }}</span> }
            }
          </div>
          <mi-status [loading]="loading()" [error]="error()"></mi-status>
          <ul class="timeline">
            @for (a of events(); track a.seq) {
              <li>
                <span class="when">#{{ a.seq }} · {{ a.occurredAt | date:'short' }}<br><strong>{{ a.actor }}</strong></span>
                <span>
                  <code>{{ a.action }}</code>
                  @if (a.caseId) { <a [routerLink]="['/cases', a.caseId]" class="muted">case {{ a.caseId }}</a> }
                  <span class="muted"> {{ detail(a) }}</span><br>
                  <span class="hash">{{ a.hash }}</span>
                </span>
              </li>
            } @empty { @if (!loading()) { <li class="empty">No audit events yet.</li> } }
          </ul>
        </mat-card-content>
      </mat-card>
    </div>
  `
})
export class AuditComponent {
  private readonly api = inject(SuiteApiService);
  readonly events = signal<AuditEvent[]>([]);
  readonly verification = signal<AuditVerification | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  constructor() { this.load(); }

  load(): void {
    this.loading.set(true); this.error.set(null);
    this.api.audit(200).subscribe({ next: e => { this.events.set(e); this.loading.set(false); }, error: e => { this.error.set(describeError(e)); this.loading.set(false); } });
  }
  verify(): void { this.api.verifyAudit().subscribe({ next: v => this.verification.set(v), error: e => this.error.set(describeError(e)) }); }
  detail(a: AuditEvent): string { return a.detail == null ? '' : typeof a.detail === 'string' ? a.detail : JSON.stringify(a.detail); }
}
