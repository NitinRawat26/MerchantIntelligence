import { Component, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { BusinessPolicy, ProhibitedBusinessResult, RestrictedCategory, WebsiteComplianceResult } from '../../shared/models';
import { FieldHintComponent, FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent, outcomeClass } from '../../shared/ui';

const POLICY_SCORE: Record<BusinessPolicy, number> = { Acceptable: 100, HighRisk: 60, Restricted: 35, Prohibited: 0 };

@Component({
  selector: 'mi-precheck',
  standalone: true,
  imports: [ReactiveFormsModule, DecimalPipe, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatTableModule, MatTabsModule, MatTooltipModule,
    FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent, FieldHintComponent],
  templateUrl: './precheck.component.html',
  styleUrl: '../platform/platform.scss'
})
export class PrecheckComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly outcomeClass = outcomeClass;
  readonly checkColumns = ['status', 'title', 'detail'];
  readonly tabs = ['website', 'prohibited'];
  readonly tab = signal(Math.max(0, this.tabs.indexOf(this.route.snapshot.queryParamMap.get('tab') ?? '')));

  readonly websiteForm = this.fb.nonNullable.group({
    websiteUrl: ['https://www.apple.com', Validators.required],
    legalName: ['Apple Inc.'],
    businessDescription: ['Consumer electronics, software and online services.'],
    declaredMcc: ['5732']
  });
  readonly websiteLoading = signal(false);
  readonly websiteError = signal<string | null>(null);
  readonly website = signal<WebsiteComplianceResult | null>(null);

  readonly prohibitedForm = this.fb.nonNullable.group({
    businessDescription: ['Online pharmacy selling prescription medication without a prescription, plus CBD gummies.', Validators.required],
    text: [''],
    declaredMcc: ['5912']
  });
  readonly prohibitedLoading = signal(false);
  readonly prohibitedError = signal<string | null>(null);
  readonly prohibited = signal<ProhibitedBusinessResult | null>(null);
  readonly categories = signal<RestrictedCategory[]>([]);
  readonly categoryColumns = ['policy', 'name', 'mccs', 'keywords'];

  constructor() {
    this.api.prohibitedCategories().subscribe({ next: c => this.categories.set(c), error: () => this.categories.set([]) });
  }

  selectTab(i: number): void {
    this.tab.set(i);
    this.router.navigate([], { queryParams: { tab: this.tabs[i] }, replaceUrl: true });
  }

  scanWebsite(): void {
    if (this.websiteForm.invalid) return;
    const v = this.websiteForm.getRawValue();
    this.websiteLoading.set(true); this.websiteError.set(null); this.website.set(null);
    this.api.websiteCompliance({
      websiteUrl: v.websiteUrl.trim(), legalName: v.legalName || undefined, businessDescription: v.businessDescription || undefined,
      declaredMcc: v.declaredMcc === '' ? undefined : Number(v.declaredMcc)
    }).subscribe({
      next: r => { this.website.set(r); this.websiteLoading.set(false); },
      error: e => { this.websiteError.set(describeError(e)); this.websiteLoading.set(false); }
    });
  }

  checkProhibited(): void {
    if (this.prohibitedForm.invalid) return;
    const v = this.prohibitedForm.getRawValue();
    this.prohibitedLoading.set(true); this.prohibitedError.set(null); this.prohibited.set(null);
    this.api.prohibitedBusiness({
      businessDescription: v.businessDescription, text: v.text || undefined,
      declaredMcc: v.declaredMcc === '' ? undefined : Number(v.declaredMcc)
    }).subscribe({
      next: r => { this.prohibited.set(r); this.prohibitedLoading.set(false); },
      error: e => { this.prohibitedError.set(describeError(e)); this.prohibitedLoading.set(false); }
    });
  }

  verdictClass(v: BusinessPolicy): string { return outcomeClass(v === 'Acceptable' ? 'Approve' : v === 'Prohibited' ? 'Decline' : 'Refer'); }
  policyScore(v: BusinessPolicy): number { return POLICY_SCORE[v]; }
  policyEffect(v: BusinessPolicy): string {
    switch (v) {
      case 'Prohibited': return 'Hard stop PROHIBITED_BUSINESS → decision Decline regardless of score.';
      case 'Restricted': return 'Reason code RESTRICTED_BUSINESS; BusinessPolicy component scores 35/100 (10% weight).';
      case 'HighRisk': return 'Reason code HIGH_RISK_BUSINESS; BusinessPolicy component scores 60/100 (10% weight).';
      default: return 'No policy reason code; BusinessPolicy component scores 100/100 (10% weight).';
    }
  }
  websiteEffect(score: number): string {
    return score < 60
      ? `WebsiteCompliance component ${score}/100 (10% weight); raises WEBSITE_NON_COMPLIANT and adds up to +0.15 pricing risk.`
      : `WebsiteCompliance component ${score}/100 (10% weight); no reason code.`;
  }
  failing(r: WebsiteComplianceResult): number { return r.checks.filter(c => c.status === 'Fail').length; }
  warning(r: WebsiteComplianceResult): number { return r.checks.filter(c => c.status === 'Warn').length; }
}
