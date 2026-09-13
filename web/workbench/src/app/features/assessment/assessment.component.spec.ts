import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';
import { AssessmentComponent } from './assessment.component';
import { SuiteApiService } from '../../shared/suite-api.service';
import { AssessmentEvent, AssessmentResult } from '../../shared/models';

const steps = [{ id: 'verification', name: 'Business identity verification' }, { id: 'score', name: 'Unified risk score & policy rules' }];

function fakeResult(): AssessmentResult {
  return {
    id: 'ASMT-TEST-1', startedAt: '2026-01-01T00:00:00Z', completedAt: '2026-01-01T00:00:30Z',
    intake: { business: { legalName: 'Acme Ltd' }, owners: [], merchantCategoryCode: 5732, annualVolume: 1, averageTicket: 1, highestTicket: 1, existingRelationship: false, cardNotPresentShare: 1, offersSubscriptions: false, offersFreeTrials: false, actor: 'tester' },
    steps: [{ id: 'verification', name: 'Business identity verification', status: 'Succeeded', summary: 'Verified', durationMs: 10 }, { id: 'score', name: 'Unified risk score & policy rules', status: 'Succeeded', summary: 'Score 720', durationMs: 5 }],
    decision: { outcome: 'Approve', score: 720, tier: 'Low', coveragePercent: 80, ruleSetVersion: '1.0', summary: 'Approve. Nothing blocking.' },
    explainability: {
      headline: 'APPROVE — Acme Ltd: score 720/1000', narrative: ['Identity: verified.'],
      checkOutcomes: [{ check: 'Business identity', result: 'Verified', detail: 'GLEIF match', severity: 'Low', covered: true }],
      findings: [], scoreComponents: [{ name: 'KYB', weight: 0.2, score: 90, weighted: 18, detail: 'ok', covered: true }],
      reasonCodes: [{ code: 'KYB_VERIFIED', description: 'Entity verified', severity: 'Low', source: 'kyb' }], creditContributions: [],
      matchedRules: [{ id: 'AUTO_APPROVE', outcome: 'Approve', priority: 10, description: 'Low risk auto approve' }], decidingRule: 'AUTO_APPROVE',
      coverageGaps: ['match'], hardStops: [], analystNextSteps: ['Run MATCH.']
    }
  };
}

describe('AssessmentComponent', () => {
  let api: jasmine.SpyObj<Pick<SuiteApiService, 'runAssessment'>> & SuiteApiService;
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AssessmentComponent],
      providers: [provideRouter([{ path: 'assess', component: AssessmentComponent }, { path: 'assess/:id', component: AssessmentComponent }]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()]
    }).compileComponents();
    api = TestBed.inject(SuiteApiService) as typeof api;
    http = TestBed.inject(HttpTestingController);
  });

  function create() {
    const fixture = TestBed.createComponent(AssessmentComponent);
    fixture.detectChanges();
    http.expectOne(r => r.url.endsWith('/api/assessment/steps')).flush(steps);
    http.expectOne(r => r.url.endsWith('/api/assessment') && r.params.get('limit') === '20').flush([]);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the intake form with every section', () => {
    const fixture = create();
    const text = fixture.nativeElement.textContent as string;
    for (const label of ['Business identity', 'Beneficial owners', 'Processing profile', 'Size & footprint', 'Financial documents', 'Run full assessment']) {
      expect(text).toContain(label);
    }
    expect(fixture.componentInstance.form.valid).toBeTrue();
  });

  it('gives every intake field a hover hint explaining how it is used in decisioning', () => {
    const fixture = create();
    const icons = fixture.nativeElement.querySelectorAll('mat-icon.hint') as NodeListOf<HTMLElement>;
    expect(icons.length).toBeGreaterThanOrEqual(30);
    const c = fixture.componentInstance;
    for (const key of ['averageTicket', 'annualVolume', 'legalName', 'ownerDateOfBirth', 'bankStatement', 'createCase']) {
      expect(c.hint(key).length).toBeGreaterThan(20);
    }
    expect(c.hint('averageTicket')).toContain('Credit model');
    expect(c.hint('averageTicket')).toContain('Volume plausibility');
    expect(c.hint('nope')).toBe('');
  });

  it('streams step progress and renders the decision with a PDF link', () => {
    const fixture = create();
    const events: AssessmentEvent[] = [
      { type: 'steps', steps },
      { type: 'step', step: { id: 'verification', name: 'Business identity verification', status: 'Running', summary: 'Running…', durationMs: 0 } },
      { type: 'step', step: { id: 'verification', name: 'Business identity verification', status: 'Succeeded', summary: 'Verified', durationMs: 10 } },
      { type: 'result', result: fakeResult() }
    ];
    spyOn(api, 'runAssessment').and.returnValue(of(...events));

    fixture.componentInstance.submit();
    fixture.detectChanges();
    http.expectOne(r => r.url.endsWith('/api/assessment') && r.params.get('limit') === '20').flush([]);
    fixture.detectChanges();

    const req = (api.runAssessment as jasmine.Spy).calls.mostRecent().args[0];
    expect(req.business.legalName).toBe('Apple Inc.');
    expect(req.merchantCategoryCode).toBe(5732);
    expect(req.createCase).toBeTrue();

    const c = fixture.componentInstance;
    expect(c.running()).toBeFalse();
    expect(c.result()?.id).toBe('ASMT-TEST-1');
    expect(c.steps()['verification'].status).toBe('Succeeded');

    const el: HTMLElement = fixture.nativeElement;
    expect(el.textContent).toContain('APPROVE');
    expect(el.textContent).toContain('AUTO_APPROVE');
    expect(el.textContent).toContain('Coverage gaps: match');
    const pdf = el.querySelector<HTMLAnchorElement>('a[href$="/api/assessment/ASMT-TEST-1/pdf"]');
    expect(pdf).withContext('PDF download link').not.toBeNull();
  });

  it('surfaces stream errors without leaving the run in progress', () => {
    const fixture = create();
    spyOn(api, 'runAssessment').and.returnValue(of<AssessmentEvent>({ type: 'error', error: 'Unified scorer exploded' }));
    fixture.componentInstance.submit();
    fixture.detectChanges();
    expect(fixture.componentInstance.error()).toBe('Unified scorer exploded');
    expect(fixture.componentInstance.running()).toBeFalse();
  });

  afterEach(() => http.verify());
});
