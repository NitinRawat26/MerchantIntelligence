import { Component, input } from '@angular/core';
import { JsonPipe } from '@angular/common';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatExpansionModule } from '@angular/material/expansion';
import { Flag, RiskTier } from './models';

export function tierClass(tier: RiskTier | string | null | undefined): string {
  return `tier-${(tier ?? 'low').toString().toLowerCase()}`;
}

export function outcomeClass(v: string | null | undefined): string {
  switch (v) {
    case 'Approve': case 'Approved': case 'Pass': case 'Available': case 'Stable': case 'Champion': return 'good';
    case 'Refer': case 'Warn': case 'Moderate': case 'InReview': case 'PendingDocuments': case 'Challenger': return 'warn';
    case 'Decline': case 'Declined': case 'Fail': case 'Significant': case 'Error': return 'bad';
    default: return 'neutral';
  }
}

/** Chip list for `{code,message,severity}` flags used by every analyser. */
@Component({
  selector: 'mi-flags',
  standalone: true,
  imports: [MatChipsModule, MatIconModule, MatTooltipModule],
  template: `
    @if (flags().length) {
      <mat-chip-set aria-label="Flags">
        @for (f of flags(); track f.code) {
          <mat-chip [class]="'tier-' + f.severity.toLowerCase()" [matTooltip]="f.message">
            <mat-icon matChipAvatar>flag</mat-icon>{{ f.code.replaceAll('_', ' ') }}
          </mat-chip>
        }
      </mat-chip-set>
      @if (showMessages()) {
        <ul class="flag-messages">
          @for (f of flags(); track f.code) { <li><strong>{{ f.severity }}</strong> — {{ f.message }}</li> }
        </ul>
      }
    } @else {
      <p class="muted">{{ emptyText() }}</p>
    }
  `,
  styles: [`
    .flag-messages { margin: 10px 0 0; padding-left: 20px; color: var(--mi-text-2); font-size: 13.5px; line-height: 1.6; strong { color: var(--mi-text); font-weight: 600; } }
    .muted { color: var(--mi-text-3); margin: 0; font-size: 13px; }
  `]
})
export class FlagsComponent {
  readonly flags = input<Flag[]>([]);
  readonly showMessages = input(true);
  readonly emptyText = input('No flags raised.');
}

/** Big number + label + bar, for scores. */
@Component({
  selector: 'mi-gauge',
  standalone: true,
  imports: [MatProgressBarModule],
  template: `
    <div class="gauge">
      <div class="gauge-value">{{ value() }}<span>{{ suffix() }}</span></div>
      <div class="gauge-label">{{ label() }}</div>
      <mat-progress-bar mode="determinate" [value]="percent()" [color]="color()"></mat-progress-bar>
    </div>
  `,
  styles: [`
    .gauge { min-width: 180px; padding: 16px 18px; border-radius: var(--mi-radius-sm); background: var(--mi-surface-2); border: 1px solid var(--mi-border); }
    .gauge-value { font-size: 42px; font-weight: 700; line-height: 1; letter-spacing: -0.03em; font-variant-numeric: tabular-nums; span { font-size: 18px; color: var(--mi-text-2); margin-left: 2px; font-weight: 500; } }
    .gauge-label { color: var(--mi-text-2); font-size: 11.5px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.06em; margin: 6px 0 10px; }
  `]
})
export class GaugeComponent {
  readonly value = input.required<number | string>();
  readonly percent = input.required<number>();
  readonly label = input('');
  readonly suffix = input('');
  readonly color = input<'primary' | 'accent' | 'warn'>('primary');
}

/** Collapsible raw JSON for any response section we don't render bespoke. */
@Component({
  selector: 'mi-json',
  standalone: true,
  imports: [JsonPipe, MatExpansionModule],
  template: `
    <mat-expansion-panel class="json-panel" [expanded]="expanded()">
      <mat-expansion-panel-header><mat-panel-title>{{ title() }}</mat-panel-title></mat-expansion-panel-header>
      <pre>{{ data() | json }}</pre>
    </mat-expansion-panel>
  `,
  styles: [`
    pre { font-family: var(--mi-mono); font-size: 12px; line-height: 1.5; max-height: 420px; overflow: auto; background: #0f172a; color: #e2e8f0; padding: 14px 16px; border-radius: var(--mi-radius-sm); margin: 0; }
  `]
})
export class JsonViewComponent {
  readonly data = input<unknown>();
  readonly title = input('Raw response');
  readonly expanded = input(false);
}

/** Error / loading banner. */
@Component({
  selector: 'mi-status',
  standalone: true,
  imports: [MatIconModule, MatProgressBarModule],
  template: `
    @if (loading()) {
      <div class="status loading"><mat-progress-bar mode="indeterminate"></mat-progress-bar><span>{{ loadingText() }}</span></div>
    }
    @if (error(); as e) {
      <div class="status error"><mat-icon>error_outline</mat-icon><span>{{ e }}</span></div>
    }
  `,
  styles: [`
    .status { display: flex; flex-direction: column; gap: 10px; padding: 14px 18px; border-radius: var(--mi-radius-sm); background: var(--mi-surface); border: 1px solid var(--mi-border); box-shadow: var(--mi-shadow); }
    .status.error { flex-direction: row; align-items: center; gap: 10px; color: var(--mi-bad); background: var(--mi-bad-soft); border-color: rgba(185, 28, 28, 0.25); font-weight: 500; }
    .status.loading span { color: var(--mi-text-2); font-size: 13.5px; }
  `]
})
export class StatusComponent {
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  readonly loadingText = input('Working…');
}
