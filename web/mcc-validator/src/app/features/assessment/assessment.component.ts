import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Subscription } from 'rxjs';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { AssessmentListItem, AssessmentRequest, AssessmentResult, AssessmentStep, AssessmentStepDescriptor, Flag, StepStatus } from '../../shared/models';
import { FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent, outcomeClass, tierClass } from '../../shared/ui';

type Preset = 'clean' | 'sanctioned' | 'restricted';

@Component({
  selector: 'mi-assessment',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, DatePipe, DecimalPipe, PercentPipe, MatButtonModule, MatCardModule, MatCheckboxModule, MatChipsModule, MatExpansionModule,
    MatFormFieldModule, MatIconModule, MatInputModule, MatProgressSpinnerModule, MatSelectModule, MatTableModule, MatTabsModule, MatTooltipModule,
    FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent],
  templateUrl: './assessment.component.html',
  styleUrls: ['../platform/platform.scss', './assessment.component.scss']
})
export class AssessmentComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  readonly tierClass = tierClass;
  readonly outcomeClass = outcomeClass;

  readonly checkColumns = ['check', 'result', 'detail', 'severity'];
  readonly componentColumns = ['name', 'weight', 'score', 'weighted', 'detail'];
  readonly contributionColumns = ['feature', 'value', 'baseline', 'contribution', 'direction'];
  readonly complianceColumns = ['status', 'title', 'detail'];
  readonly stepColumns = ['status', 'name', 'summary', 'duration'];

  readonly form = this.fb.nonNullable.group({
    legalName: ['Apple Inc.', Validators.required],
    tradingName: [''],
    registrationNumber: [''],
    taxId: [''],
    addressLine: ['One Apple Park Way'],
    city: ['Cupertino'],
    region: ['CA'],
    postalCode: ['95014'],
    country: ['US'],
    websiteUrl: ['https://www.apple.com'],
    businessDescription: ['Consumer electronics, software and online services.'],
    owners: this.fb.array([this.owner('Tim Cook', 'CEO')]),
    merchantCategoryCode: [5732, [Validators.required, Validators.min(1), Validators.max(9999)]],
    annualVolume: [1_200_000, [Validators.required, Validators.min(0)]],
    averageTicket: [85, [Validators.required, Validators.min(0.01)]],
    highestTicket: [1500, [Validators.required, Validators.min(0)]],
    existingRelationship: [false],
    deliveryDays: ['' as string | number],
    cardNotPresentShare: [1, [Validators.min(0), Validators.max(1)]],
    offersSubscriptions: [false],
    offersFreeTrials: [false],
    employeeCount: ['' as string | number],
    yearsInBusiness: ['' as string | number],
    priorYearRevenue: ['' as string | number],
    websiteProductCount: ['' as string | number],
    hasPhysicalLocation: ['' as '' | 'true' | 'false'],
    bankStatementCsv: [''],
    financialStatementText: [''],
    externalRef: [''],
    actor: ['analyst', Validators.required],
    createCase: [true]
  });

  readonly bankFile = signal<File | null>(null);
  readonly financialFile = signal<File | null>(null);

  readonly catalog = signal<AssessmentStepDescriptor[]>([]);
  readonly steps = signal<Record<string, AssessmentStep>>({});
  readonly running = signal(false);
  readonly error = signal<string | null>(null);
  readonly result = signal<AssessmentResult | null>(null);
  readonly history = signal<AssessmentListItem[]>([]);
  readonly loadingHistory = signal(false);

  readonly progress = computed(() => {
    const total = this.catalog().length || 1;
    const done = Object.values(this.steps()).filter(s => s.status !== 'Running').length;
    return Math.round((done / total) * 100);
  });
  readonly hasSteps = computed(() => Object.keys(this.steps()).length > 0);
  readonly currentStep = computed(() => Object.values(this.steps()).find(s => s.status === 'Running') ?? null);
  readonly stepList = computed(() => this.catalog().map(c => this.steps()[c.id] ?? { id: c.id, name: c.name, status: 'Pending' as StepStatus, summary: '', durationMs: 0 }));

  private run?: Subscription;

  constructor() {
    this.api.assessmentSteps().pipe(takeUntilDestroyed()).subscribe({ next: s => this.catalog.set(s), error: () => { /* rendered once the stream sends the catalogue */ } });
    this.loadHistory();
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(p => {
      const id = p.get('id');
      if (id && id !== this.result()?.id) this.open(id);
    });
    this.destroyRef.onDestroy(() => this.run?.unsubscribe());
  }

  get owners(): FormArray { return this.form.controls.owners; }
  private owner(fullName = '', role = '') { return this.fb.nonNullable.group({ fullName: [fullName], dateOfBirth: [''], nationality: [''], role: [role], ownershipPercent: [''] }); }
  addOwner(): void { this.owners.push(this.owner()); }
  removeOwner(i: number): void { this.owners.removeAt(i); }

  onFile(kind: 'bank' | 'financial', event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0] ?? null;
    (kind === 'bank' ? this.bankFile : this.financialFile).set(file);
  }
  clearFile(kind: 'bank' | 'financial'): void { (kind === 'bank' ? this.bankFile : this.financialFile).set(null); }

  preset(p: Preset): void {
    this.owners.clear();
    switch (p) {
      case 'clean':
        this.form.patchValue({ legalName: 'Apple Inc.', tradingName: '', country: 'US', addressLine: 'One Apple Park Way', city: 'Cupertino', region: 'CA', postalCode: '95014',
          websiteUrl: 'https://www.apple.com', businessDescription: 'Consumer electronics, software and online services.', merchantCategoryCode: 5732,
          annualVolume: 1_200_000, averageTicket: 85, highestTicket: 1500, employeeCount: 160000, yearsInBusiness: 48, hasPhysicalLocation: 'true', offersSubscriptions: false, offersFreeTrials: false });
        this.owners.push(this.owner('Tim Cook', 'CEO'));
        break;
      case 'sanctioned':
        this.form.patchValue({ legalName: 'Rosneft Oil Company', tradingName: 'Rosneft', country: 'RU', addressLine: 'Sofiyskaya Embankment 26/1', city: 'Moscow', region: '', postalCode: '117997',
          websiteUrl: '', businessDescription: 'Oil and gas exploration, refining and fuel retail.', merchantCategoryCode: 5541,
          annualVolume: 50_000_000, averageTicket: 60, highestTicket: 5000, employeeCount: 300000, yearsInBusiness: 30, hasPhysicalLocation: 'true' });
        this.owners.push(this.owner('Viktor Bout', 'Director'));
        this.owners.at(0).patchValue({ dateOfBirth: '1967-01-13', nationality: 'RU' });
        break;
      case 'restricted':
        this.form.patchValue({ legalName: 'Green Leaf Wellness LLC', tradingName: 'GreenLeaf CBD', country: 'US', addressLine: '12 Market St', city: 'Denver', region: 'CO', postalCode: '80202',
          websiteUrl: '', businessDescription: 'Online store selling CBD oil, hemp gummies and kratom with free-trial subscription boxes; supplements ship in 21 days.', merchantCategoryCode: 5912,
          annualVolume: 4_800_000, averageTicket: 45, highestTicket: 900, employeeCount: 2, yearsInBusiness: 0.5, hasPhysicalLocation: 'false', offersSubscriptions: true, offersFreeTrials: true, deliveryDays: 21 });
        this.owners.push(this.owner('Jane Doe', 'Owner'));
        break;
    }
  }

  submit(): void {
    if (this.form.invalid || this.running()) return;
    const v = this.form.getRawValue();
    const opt = (s: string) => (s.trim() ? s.trim() : undefined);
    const num = (s: string | number) => (s === '' || s === null ? null : Number(s));
    const req: AssessmentRequest = {
      business: {
        legalName: v.legalName, tradingName: opt(v.tradingName), registrationNumber: opt(v.registrationNumber), taxId: opt(v.taxId),
        addressLine: opt(v.addressLine), city: opt(v.city), region: opt(v.region), postalCode: opt(v.postalCode), country: opt(v.country), websiteUrl: opt(v.websiteUrl)
      },
      owners: v.owners.filter(o => o.fullName.trim()).map(o => ({
        fullName: o.fullName.trim(), dateOfBirth: opt(o.dateOfBirth), nationality: opt(o.nationality), role: opt(o.role),
        ownershipPercent: o.ownershipPercent === '' ? undefined : Number(o.ownershipPercent)
      })),
      businessDescription: opt(v.businessDescription),
      merchantCategoryCode: Number(v.merchantCategoryCode), annualVolume: Number(v.annualVolume), averageTicket: Number(v.averageTicket), highestTicket: Number(v.highestTicket),
      existingRelationship: v.existingRelationship, deliveryDays: num(v.deliveryDays), cardNotPresentShare: Number(v.cardNotPresentShare),
      offersSubscriptions: v.offersSubscriptions, offersFreeTrials: v.offersFreeTrials,
      employeeCount: num(v.employeeCount), yearsInBusiness: num(v.yearsInBusiness), priorYearRevenue: num(v.priorYearRevenue), websiteProductCount: num(v.websiteProductCount),
      hasPhysicalLocation: v.hasPhysicalLocation === '' ? null : v.hasPhysicalLocation === 'true',
      bankStatementCsv: this.bankFile() ? null : opt(v.bankStatementCsv) ?? null,
      financialStatementText: this.financialFile() ? null : opt(v.financialStatementText) ?? null,
      externalRef: opt(v.externalRef) ?? null, actor: v.actor.trim() || 'analyst', createCase: v.createCase
    };

    this.running.set(true); this.error.set(null); this.result.set(null); this.steps.set({});
    this.run?.unsubscribe();
    this.run = this.api.runAssessment(req, { bankStatement: this.bankFile(), financialStatement: this.financialFile() }).subscribe({
      next: ev => {
        switch (ev.type) {
          case 'steps': if (!this.catalog().length) this.catalog.set(ev.steps); break;
          case 'step': this.steps.update(s => ({ ...s, [ev.step.id]: ev.step })); break;
          case 'result':
            this.result.set(ev.result);
            this.router.navigate(['/assess', ev.result.id], { replaceUrl: true });
            this.loadHistory();
            queueMicrotask(() => document.getElementById('assessment-result')?.scrollIntoView({ behavior: 'smooth', block: 'start' }));
            break;
          case 'error': this.error.set(ev.error); break;
        }
      },
      error: e => { this.error.set(describeError(e)); this.running.set(false); },
      complete: () => this.running.set(false)
    });
  }

  cancel(): void { this.run?.unsubscribe(); this.running.set(false); this.error.set('Assessment cancelled.'); }

  open(id: string): void {
    this.error.set(null);
    this.api.assessment(id).subscribe({
      next: r => { this.result.set(r); this.steps.set(Object.fromEntries(r.steps.map(s => [s.id, s]))); },
      error: e => this.error.set(describeError(e))
    });
  }

  newAssessment(): void { this.result.set(null); this.steps.set({}); this.error.set(null); this.router.navigate(['/assess']); }

  loadHistory(): void {
    this.loadingHistory.set(true);
    this.api.assessments(20).subscribe({ next: h => { this.history.set(h); this.loadingHistory.set(false); }, error: () => this.loadingHistory.set(false) });
  }

  pdfUrl(id: string): string { return this.api.assessmentPdfUrl(id); }

  stepIcon(status: StepStatus): string {
    switch (status) {
      case 'Succeeded': return 'check_circle';
      case 'Failed': return 'error';
      case 'Skipped': return 'remove_circle_outline';
      case 'Running': return 'autorenew';
      default: return 'radio_button_unchecked';
    }
  }

  severityClass(sev: string, covered = true): string { return covered ? `text-${sev.toLowerCase()}` : 'text-gap'; }
  outcomeCardClass(outcome: string): string { return outcomeClass(outcome); }
  abs(n: number): number { return Math.abs(n); }
  coveredChecks(r: AssessmentResult): number { return r.explainability.checkOutcomes.filter(o => o.covered).length; }
  reasonFlags(r: AssessmentResult): Flag[] { return r.explainability.reasonCodes.map(c => ({ code: c.code, message: `${c.description} [${c.source}]`, severity: c.severity })); }
  findingFlags(r: AssessmentResult): Flag[] { return r.explainability.findings.map(f => ({ code: f.code, message: `${f.message} [${f.source}]`, severity: f.severity })); }
  statementEntries(s: Record<string, number | null | undefined>): { key: string; value: number }[] {
    return Object.entries(s).filter(([, v]) => typeof v === 'number').map(([key, value]) => ({ key, value: value as number }));
  }
}
