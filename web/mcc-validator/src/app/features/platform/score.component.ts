import { Component, inject, signal } from '@angular/core';
import { DecimalPipe, PercentPipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { UnifiedScoreRequest, UnifiedScoreResponse } from '../../shared/models';
import { FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent, outcomeClass, tierClass } from '../../shared/ui';

const nullIfBlank = (v: unknown) => (v === '' || v === null || v === undefined ? null : v);

@Component({
  selector: 'mi-score',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, DecimalPipe, PercentPipe, MatButtonModule, MatCardModule, MatCheckboxModule, MatChipsModule, MatFormFieldModule,
    MatIconModule, MatInputModule, MatSelectModule, MatTableModule, MatTooltipModule, FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent],
  templateUrl: './score.component.html',
  styleUrl: './platform.scss'
})
export class ScoreComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  readonly tierClass = tierClass;
  readonly outcomeClass = outcomeClass;

  readonly form = this.fb.nonNullable.group({
    merchantName: ['Acme Widgets LLC'],
    externalRef: [''],
    actor: ['analyst', Validators.required],
    createCase: [false],
    includeApplication: [true],
    merchantCategoryCode: [5999, [Validators.min(1), Validators.max(9999)]],
    annualVolume: [480000, Validators.min(0)],
    averageTicket: [85, Validators.min(0)],
    highestTicket: [1200, Validators.min(0)],
    matchFound: [false],
    existingRelationship: [false],
    kybRisk: [''],
    businessVerified: [''],
    entityAgeMonths: [''],
    sanctionsMatch: [''],
    pepMatch: [''],
    adverseMedia: [''],
    prohibitedVerdict: [''],
    websiteComplianceScore: [''],
    volumePlausibilityScore: [''],
    termsRiskBand: [''],
    matchListed: ['']
  });

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly result = signal<UnifiedScoreResponse | null>(null);
  readonly componentColumns = ['name', 'weight', 'score', 'weighted', 'detail'];

  presets = {
    clean: () => this.form.patchValue({ kybRisk: 'Low', businessVerified: 'true', entityAgeMonths: '84', sanctionsMatch: 'false', pepMatch: 'false', adverseMedia: 'false', prohibitedVerdict: 'Acceptable', websiteComplianceScore: '92', volumePlausibilityScore: '88', termsRiskBand: 'B', matchListed: 'false', matchFound: false }),
    sanctioned: () => this.form.patchValue({ kybRisk: 'High', businessVerified: 'true', sanctionsMatch: 'true', pepMatch: 'false', prohibitedVerdict: 'Acceptable', matchListed: 'false' }),
    sparse: () => this.form.patchValue({ kybRisk: '', businessVerified: '', entityAgeMonths: '', sanctionsMatch: '', pepMatch: '', adverseMedia: '', prohibitedVerdict: '', websiteComplianceScore: '', volumePlausibilityScore: '', termsRiskBand: '', matchListed: '' })
  };

  submit(): void {
    if (this.form.invalid) return;
    const v = this.form.getRawValue();
    const bool = (s: string) => (s === '' ? null : s === 'true');
    const num = (s: string) => (s === '' ? null : Number(s));
    const req: UnifiedScoreRequest = {
      application: v.includeApplication ? {
        merchantCategoryCode: v.merchantCategoryCode, annualVolume: v.annualVolume, averageTicket: v.averageTicket,
        highestTicket: v.highestTicket, matchFound: v.matchFound, existingRelationship: v.existingRelationship
      } : null,
      kybRisk: nullIfBlank(v.kybRisk) as UnifiedScoreRequest['kybRisk'],
      businessVerified: bool(v.businessVerified),
      entityAgeMonths: num(v.entityAgeMonths),
      sanctionsMatch: bool(v.sanctionsMatch),
      pepMatch: bool(v.pepMatch),
      adverseMedia: bool(v.adverseMedia),
      prohibitedVerdict: nullIfBlank(v.prohibitedVerdict) as UnifiedScoreRequest['prohibitedVerdict'],
      websiteComplianceScore: num(v.websiteComplianceScore),
      volumePlausibilityScore: num(v.volumePlausibilityScore),
      termsRiskBand: nullIfBlank(v.termsRiskBand) as string | null,
      matchFound: bool(v.matchListed),
      createCase: v.createCase,
      merchantName: nullIfBlank(v.merchantName) as string | null,
      externalRef: nullIfBlank(v.externalRef) as string | null,
      actor: v.actor
    };
    this.loading.set(true); this.error.set(null); this.result.set(null);
    this.api.score(req).subscribe({
      next: r => { this.result.set(r); this.loading.set(false); },
      error: e => { this.error.set(describeError(e)); this.loading.set(false); }
    });
  }

  reasonFlags(r: UnifiedScoreResponse) {
    return r.score.reasonCodes.map(c => ({ code: c.code, message: `${c.source}: ${c.description}`, severity: c.severity }));
  }
}
