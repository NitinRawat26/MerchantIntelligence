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
    .flag-messages { margin: 8px 0 0; padding-left: 20px; color: #444; font-size: 14px; }
    .muted { color: #777; margin: 0; }
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
    .gauge { min-width: 180px; }
    .gauge-value { font-size: 40px; font-weight: 700; line-height: 1; font-variant-numeric: tabular-nums; span { font-size: 18px; color: #666; margin-left: 2px; } }
    .gauge-label { color: #666; font-size: 12px; text-transform: uppercase; letter-spacing: 0.06em; margin: 4px 0 8px; }
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
    pre { font-size: 12px; line-height: 1.4; max-height: 420px; overflow: auto; background: #fafafa; padding: 12px; border-radius: 6px; margin: 0; }
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
    .status { display: flex; flex-direction: column; gap: 8px; padding: 12px 16px; border-radius: 8px; background: #fff; border: 1px solid #e0e0e0; }
    .status.error { flex-direction: row; align-items: center; color: #b3261e; border-color: #b3261e; }
    .status.loading span { color: #555; font-size: 14px; }
  `]
})
export class StatusComponent {
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  readonly loadingText = input('Working…');
}
