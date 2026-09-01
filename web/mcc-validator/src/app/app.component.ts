import { Component, computed, inject, signal } from '@angular/core';
import { AsyncPipe, DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { startWith } from 'rxjs';
import { HistoryEntry, MccCatalogItem, MccValidationResult, MccVerdict, RiskTier } from './mcc-validation.models';
import { MccValidationService } from './mcc-validation.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    AsyncPipe, DatePipe, DecimalPipe, PercentPipe, ReactiveFormsModule,
    MatAutocompleteModule, MatButtonModule, MatCardModule, MatChipsModule, MatExpansionModule,
    MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatProgressSpinnerModule,
    MatTableModule, MatToolbarModule, MatTooltipModule
  ],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent {
  private readonly api = inject(MccValidationService);
  private readonly fb = inject(FormBuilder);

  readonly form = this.fb.nonNullable.group({
    mcc: ['', [Validators.required, Validators.pattern(/^\d{4}$/)]],
    websiteUrl: ['', [Validators.required, Validators.pattern(/^(https?:\/\/)?[\w.-]+\.[a-z]{2,}(\/.*)?$/i)]]
  });

  readonly catalog = toSignal(this.api.catalog$, { initialValue: [] as MccCatalogItem[] });
  private readonly mccInput = toSignal(this.form.controls.mcc.valueChanges.pipe(startWith('')), { initialValue: '' });

  readonly filteredCatalog = computed(() => {
    const q = (this.mccInput() ?? '').toString().toLowerCase().trim();
    const items = this.catalog();
    if (!q) return items.slice(0, 50);
    return items
      .filter(i => i.mcc.toString().startsWith(q) || i.description.toLowerCase().includes(q) || i.category.toLowerCase().includes(q))
      .slice(0, 50);
  });

  readonly selectedEntry = computed(() => {
    const code = Number(this.mccInput());
    return this.catalog().find(i => i.mcc === code) ?? null;
  });

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly result = signal<MccValidationResult | null>(null);
  readonly history = signal<HistoryEntry[]>([]);
  readonly historyColumns = ['at', 'mcc', 'website', 'verdict', 'accuracy', 'suggested'];

  readonly examples = [
    { mcc: '5045', websiteUrl: 'https://www.apple.com' },
    { mcc: '5812', websiteUrl: 'https://www.mcdonalds.com' },
    { mcc: '7011', websiteUrl: 'https://www.marriott.com' },
    { mcc: '5411', websiteUrl: 'https://www.draftkings.com' }
  ];

  displayMcc = (value: string | number | null): string => (value == null ? '' : value.toString());

  useExample(example: { mcc: string; websiteUrl: string }): void {
    this.form.setValue(example);
    this.submit();
  }

  submit(): void {
    if (this.form.invalid || this.loading()) {
      this.form.markAllAsTouched();
      return;
    }

    const request = { mcc: Number(this.form.controls.mcc.value), websiteUrl: this.form.controls.websiteUrl.value.trim() };
    this.loading.set(true);
    this.error.set(null);

    this.api.validate(request).subscribe({
      next: result => {
        this.result.set(result);
        this.history.update(h => [{ at: new Date(), request, result }, ...h].slice(0, 20));
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        const detail = err.error?.errors ? Object.values(err.error.errors as Record<string, string[]>).flat().join(' ') : err.error?.title;
        this.error.set(detail || err.message || 'Validation failed.');
        this.loading.set(false);
      }
    });
  }

  reload(entry: HistoryEntry): void {
    this.form.setValue({ mcc: entry.request.mcc.toString(), websiteUrl: entry.request.websiteUrl });
    this.result.set(entry.result);
    this.error.set(null);
  }

  verdictIcon(verdict: MccVerdict): string {
    return { Consistent: 'verified', Questionable: 'help', Inconsistent: 'report', Insufficient: 'visibility_off' }[verdict];
  }

  verdictLabel(verdict: MccVerdict): string {
    return {
      Consistent: 'MCC matches the business',
      Questionable: 'MCC is plausible but needs review',
      Inconsistent: 'MCC does not match the business',
      Insufficient: 'Not enough evidence'
    }[verdict];
  }

  verdictClass(verdict: MccVerdict): string {
    return `verdict-${verdict.toLowerCase()}`;
  }

  tierClass(tier: RiskTier): string {
    return `tier-${tier.toLowerCase()}`;
  }

  gaugeColor(percent: number): 'primary' | 'accent' | 'warn' {
    return percent >= 55 ? 'primary' : percent >= 25 ? 'accent' : 'warn';
  }
}
