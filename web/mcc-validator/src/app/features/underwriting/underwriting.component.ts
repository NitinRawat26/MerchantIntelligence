import { Component, inject, signal } from '@angular/core';
import { CurrencyPipe, DecimalPipe, PercentPipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable } from 'rxjs';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { CashFlowAnalysis, Decision, DecisionExplanation, FinancialStatementAnalysis, TermsRecommendation, VolumePlausibilityResult } from '../../shared/models';
import { FieldHintComponent, FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent, outcomeClass } from '../../shared/ui';

const SAMPLE_CSV = `Date,Description,Amount,Balance
2024-01-03,STRIPE TRANSFER,4820.15,12450.15
2024-01-10,PAYROLL ADP,-3200.00,9250.15
2024-01-17,STRIPE TRANSFER,5110.40,14360.55
2024-01-25,RENT ACH,-2500.00,11860.55
2024-02-02,STRIPE TRANSFER,4675.90,16536.45
2024-02-09,PAYROLL ADP,-3200.00,13336.45
2024-02-15,SBA LOAN PAYMENT,-850.00,12486.45
2024-02-21,STRIPE TRANSFER,5320.10,17806.55
2024-03-01,STRIPE TRANSFER,6010.75,23817.30
2024-03-08,PAYROLL ADP,-3200.00,20617.30
2024-03-15,NSF FEE,-35.00,20582.30
2024-03-22,OWNER DRAW,-4000.00,16582.30`;

const SAMPLE_PNL = `Revenue,1250000
Cost of goods sold,610000
Gross profit,640000
Operating expenses,455000
Operating income,185000
Interest expense,22000
Depreciation,18000
Net income,131000
Total assets,720000
Current assets,310000
Cash,95000
Total liabilities,380000
Current liabilities,190000
Total debt,240000
Equity,340000`;

interface Panel<T> { loading: boolean; error: string | null; result: T | null; }
const empty = <T>(): Panel<T> => ({ loading: false, error: null, result: null });

@Component({
  selector: 'mi-underwriting',
  standalone: true,
  imports: [ReactiveFormsModule, CurrencyPipe, DecimalPipe, PercentPipe, MatButtonModule, MatCardModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSelectModule, MatSliderModule, MatTableModule, MatTabsModule, MatTooltipModule, FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent, FieldHintComponent],
  templateUrl: './underwriting.component.html',
  styleUrl: '../platform/platform.scss'
})
export class UnderwritingComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  readonly outcomeClass = outcomeClass;
  readonly decisions: Decision[] = ['Approved', 'Declined', 'Cancelled'];
  readonly metricColumns = ['name', 'value', 'benchmark', 'assessment'];
  readonly monthColumns = ['month', 'inflows', 'outflows', 'net', 'card', 'balance'];

  readonly app = this.fb.nonNullable.group({
    merchantCategoryCode: [5999, [Validators.required, Validators.min(1), Validators.max(9999)]],
    annualVolume: [480000, [Validators.required, Validators.min(0)]],
    averageTicket: [85, [Validators.required, Validators.min(0)]],
    highestTicket: [1200, [Validators.required, Validators.min(0)]],
    matchFound: [false],
    existingRelationship: [false]
  });
  readonly explainClass = this.fb.nonNullable.control<'' | Decision>('');
  readonly terms = this.fb.nonNullable.group({
    deliveryDays: ['' as string | number], cardNotPresentShare: [0.8, [Validators.min(0), Validators.max(1)]], kybHighRisk: [''],
    websiteComplianceScore: ['' as string | number], volumePlausibilityScore: ['' as string | number], offersSubscriptions: [false], offersFreeTrials: [false]
  });
  readonly plaus = this.fb.nonNullable.group({
    employeeCount: ['' as string | number], locationCount: ['' as string | number], yearsInBusiness: ['' as string | number], priorYearRevenue: ['' as string | number],
    monthlyCardVolumeFromStatements: ['' as string | number], websiteProductCount: ['' as string | number], hasPhysicalLocation: ['']
  });
  readonly bankCsv = this.fb.nonNullable.control(SAMPLE_CSV);
  readonly pnlText = this.fb.nonNullable.control(SAMPLE_PNL);
  readonly declaredVolume = this.fb.nonNullable.control<string | number>(1000000);

  readonly explain = signal(empty<DecisionExplanation>());
  readonly termsResult = signal(empty<TermsRecommendation>());
  readonly plausResult = signal(empty<VolumePlausibilityResult>());
  readonly bank = signal(empty<CashFlowAnalysis>());
  readonly pnl = signal(empty<FinancialStatementAnalysis>());

  private run<T>(panel: ReturnType<typeof signal<Panel<T>>>, obs: Observable<T>): void {
    panel.set({ loading: true, error: null, result: null });
    obs.subscribe({ next: r => panel.set({ loading: false, error: null, result: r }), error: e => panel.set({ loading: false, error: describeError(e), result: null }) });
  }
  private num(v: string | number): number | null { return v === '' ? null : Number(v); }
  private bool(v: string): boolean | null { return v === '' ? null : v === 'true'; }

  doExplain(): void {
    if (this.app.invalid) return;
    this.run(this.explain, this.api.explain({ ...this.app.getRawValue(), explainClass: this.explainClass.value || null }));
  }
  doTerms(): void {
    if (this.app.invalid || this.terms.invalid) return;
    const t = this.terms.getRawValue();
    this.run(this.termsResult, this.api.recommendTerms({
      ...this.app.getRawValue(), deliveryDays: this.num(t.deliveryDays), cardNotPresentShare: t.cardNotPresentShare, kybHighRisk: this.bool(t.kybHighRisk),
      websiteComplianceScore: this.num(t.websiteComplianceScore), volumePlausibilityScore: this.num(t.volumePlausibilityScore),
      offersSubscriptions: t.offersSubscriptions, offersFreeTrials: t.offersFreeTrials
    }));
  }
  doPlausibility(): void {
    if (this.app.invalid) return;
    const a = this.app.getRawValue(); const p = this.plaus.getRawValue();
    this.run(this.plausResult, this.api.volumePlausibility({
      annualVolume: a.annualVolume, averageTicket: a.averageTicket, highestTicket: a.highestTicket, merchantCategoryCode: a.merchantCategoryCode,
      employeeCount: this.num(p.employeeCount), locationCount: this.num(p.locationCount), yearsInBusiness: this.num(p.yearsInBusiness), priorYearRevenue: this.num(p.priorYearRevenue),
      monthlyCardVolumeFromStatements: this.num(p.monthlyCardVolumeFromStatements), websiteProductCount: this.num(p.websiteProductCount), hasPhysicalLocation: this.bool(p.hasPhysicalLocation)
    }));
  }
  doBankCsv(): void { this.run(this.bank, this.api.bankStatementCsv(this.bankCsv.value)); }
  doBankFile(ev: Event): void {
    const file = (ev.target as HTMLInputElement).files?.[0];
    if (file) this.run(this.bank, this.api.bankStatementFile(file));
  }
  doPnlText(): void { this.run(this.pnl, this.api.financialStatementText(this.pnlText.value, this.num(this.declaredVolume.value) ?? undefined)); }
  doPnlFile(ev: Event): void {
    const file = (ev.target as HTMLInputElement).files?.[0];
    if (file) this.run(this.pnl, this.api.financialStatementFile(file, this.num(this.declaredVolume.value) ?? undefined));
  }

  barWidth(value: number, items: { contribution: number }[]): number {
    const max = Math.max(0.0001, ...items.map(i => Math.abs(i.contribution)));
    return 50 * Math.abs(value) / max;
  }
  statementLines(s: Record<string, number | null | undefined>): { label: string; value: number }[] {
    return Object.entries(s).filter(([k, v]) => k !== 'rawLines' && typeof v === 'number').map(([k, v]) => ({ label: k.replace(/([A-Z])/g, ' $1').replace(/^./, c => c.toUpperCase()), value: v as number }));
  }
}
