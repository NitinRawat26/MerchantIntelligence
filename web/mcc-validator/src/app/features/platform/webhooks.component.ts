import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { WebhookDelivery, WebhookSubscription } from '../../shared/models';
import { StatusComponent } from '../../shared/ui';

@Component({
  selector: 'mi-webhooks',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTableModule, StatusComponent],
  styleUrl: './platform.scss',
  template: `
    <div class="page">
      <mat-card appearance="outlined">
        <mat-card-header>
          <mat-icon mat-card-avatar>webhook</mat-icon>
          <mat-card-title>Webhooks</mat-card-title>
          <mat-card-subtitle>HMAC-SHA256 signed (<code>X-MI-Signature</code>), 3 attempts with backoff · secrets are write-only</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <form [formGroup]="form" (ngSubmit)="register()" class="grid-4">
            <mat-form-field appearance="outline" class="wide-2"><mat-label>Endpoint URL</mat-label><input matInput formControlName="url" placeholder="https://example.com/hooks/mi" required></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Secret (≥16 chars)</mat-label><input matInput type="password" formControlName="secret" required></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Events</mat-label>
              <mat-select formControlName="events" multiple>@for (e of events(); track e) {<mat-option [value]="e">{{ e }}</mat-option>}</mat-select></mat-form-field>
            <div class="actions wide"><button mat-flat-button color="primary" type="submit" [disabled]="form.invalid">Register webhook</button>
              @if (message(); as m) { <span [class]="m.ok ? 'text-low' : 'text-high'">{{ m.text }}</span> }</div>
          </form>
          <mi-status [loading]="loading()" [error]="error()"></mi-status>
          <table mat-table [dataSource]="subs()" class="full">
            <ng-container matColumnDef="url"><th mat-header-cell *matHeaderCellDef>URL</th><td mat-cell *matCellDef="let s">{{ s.url }}<br><span class="muted">{{ s.id }}</span></td></ng-container>
            <ng-container matColumnDef="events"><th mat-header-cell *matHeaderCellDef>Events</th><td mat-cell *matCellDef="let s">{{ s.events.join(', ') }}</td></ng-container>
            <ng-container matColumnDef="created"><th mat-header-cell *matHeaderCellDef>Created</th><td mat-cell *matCellDef="let s">{{ s.createdAt | date:'short' }}</td></ng-container>
            <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th><td mat-cell *matCellDef="let s"><button mat-icon-button color="warn" type="button" (click)="remove(s)" aria-label="Delete"><mat-icon>delete</mat-icon></button></td></ng-container>
            <tr mat-header-row *matHeaderRowDef="subColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: subColumns"></tr>
          </table>
          @if (!subs().length && !loading()) { <p class="empty">No webhooks registered.</p> }
        </mat-card-content>
      </mat-card>

      <mat-card appearance="outlined">
        <mat-card-header><mat-card-title>Recent deliveries</mat-card-title>
          <span class="spacer"></span><button mat-stroked-button type="button" (click)="loadDeliveries()"><mat-icon>refresh</mat-icon> Refresh</button></mat-card-header>
        <mat-card-content>
          <table mat-table [dataSource]="deliveries()" class="full">
            <ng-container matColumnDef="event"><th mat-header-cell *matHeaderCellDef>Event</th><td mat-cell *matCellDef="let d"><code>{{ d.event }}</code></td></ng-container>
            <ng-container matColumnDef="webhook"><th mat-header-cell *matHeaderCellDef>Webhook</th><td mat-cell *matCellDef="let d" class="detail">{{ d.webhookId }}</td></ng-container>
            <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Result</th><td mat-cell *matCellDef="let d"><span class="pill small" [class]="d.delivered ? 'good' : 'bad'">{{ d.delivered ? 'delivered' : 'failed' }}</span> {{ d.statusCode ?? '' }} <span class="muted">{{ d.error }}</span></td></ng-container>
            <ng-container matColumnDef="attempts"><th mat-header-cell *matHeaderCellDef>Attempts</th><td mat-cell *matCellDef="let d">{{ d.attempts }}</td></ng-container>
            <ng-container matColumnDef="when"><th mat-header-cell *matHeaderCellDef>Last attempt</th><td mat-cell *matCellDef="let d">{{ (d.lastAttemptAt ?? d.createdAt) | date:'short' }}</td></ng-container>
            <tr mat-header-row *matHeaderRowDef="delColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: delColumns"></tr>
          </table>
          @if (!deliveries().length) { <p class="empty">No deliveries yet — case and rules events will appear here.</p> }
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`.wide-2 { grid-column: span 2; } .spacer { flex: 1; } @media (max-width: 640px) { .wide-2 { grid-column: auto; } }`]
})
export class WebhooksComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  readonly subColumns = ['url', 'events', 'created', 'actions'];
  readonly delColumns = ['event', 'webhook', 'status', 'attempts', 'when'];
  readonly form = this.fb.nonNullable.group({
    url: ['', [Validators.required, Validators.pattern(/^https?:\/\/.+/)]],
    secret: ['', [Validators.required, Validators.minLength(16)]],
    events: [['case.created', 'case.decided'] as string[], Validators.required]
  });
  readonly subs = signal<WebhookSubscription[]>([]);
  readonly deliveries = signal<WebhookDelivery[]>([]);
  readonly events = signal<string[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<{ ok: boolean; text: string } | null>(null);

  constructor() { this.load(); this.loadDeliveries(); this.api.webhookEvents().subscribe({ next: e => this.events.set(e), error: () => {} }); }

  load(): void {
    this.loading.set(true); this.error.set(null);
    this.api.webhooks().subscribe({ next: s => { this.subs.set(s); this.loading.set(false); }, error: e => { this.error.set(describeError(e)); this.loading.set(false); } });
  }
  loadDeliveries(): void { this.api.deliveries().subscribe({ next: d => this.deliveries.set(d), error: () => {} }); }
  register(): void {
    const v = this.form.getRawValue(); this.message.set(null);
    this.api.registerWebhook(v.url, v.secret, v.events).subscribe({
      next: () => { this.message.set({ ok: true, text: 'Registered' }); this.form.controls.secret.reset(''); this.load(); },
      error: e => this.message.set({ ok: false, text: describeError(e) })
    });
  }
  remove(s: WebhookSubscription): void { this.api.removeWebhook(s.id).subscribe({ next: () => this.load(), error: e => this.message.set({ ok: false, text: describeError(e) }) }); }
}
