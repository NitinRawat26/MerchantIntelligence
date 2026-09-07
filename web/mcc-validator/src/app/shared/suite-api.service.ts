import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  AuditEvent, AuditVerification, CaseNote, CaseQueueStats, CaseStatus, CashFlowAnalysis, ChampionChallengerReport, DecisionExplanation,
  DriftReport, ExplainRequest, FinancialStatementAnalysis, FullKybRequest, KybReport, LoggedDecision, MatchInquiryRequest, MatchResult,
  MerchantCase, ModelsResponse, RegisteredModel, RetrainResult, RuleSet, RuleSetVersion, RulesEvaluation, TermsRecommendation, TermsRequest,
  UnifiedScoreRequest, UnifiedScoreResponse, VolumePlausibilityRequest, VolumePlausibilityResult, WebhookDelivery, WebhookSubscription, Decision, CasePriority, RuleOutcome,
  AssessmentEvent, AssessmentListItem, AssessmentRequest, AssessmentResult, AssessmentStepDescriptor
} from './models';

/** Flattens ASP.NET ProblemDetails / validation errors and our `{ error }` bodies into one line. */
export function describeError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error;
    if (body?.errors) return Object.values(body.errors as Record<string, string[]>).flat().join(' ');
    if (typeof body?.error === 'string') return body.error;
    if (typeof body?.detail === 'string') return body.detail;
    if (typeof body?.title === 'string') return body.title;
    if (typeof body === 'string' && body) return body;
    if (err.status === 0) return 'API unreachable – is the .NET API running on port 5292?';
    return `${err.status} ${err.statusText}`;
  }
  return err instanceof Error ? err.message : String(err);
}

@Injectable({ providedIn: 'root' })
export class SuiteApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBaseUrl}/api`;

  // KYB
  kybReport(req: FullKybRequest): Observable<KybReport> { return this.http.post<KybReport>(`${this.base}/kyb/report`, req); }
  sanctionsLists(): Observable<unknown[]> { return this.http.get<unknown[]>(`${this.base}/kyb/screen/lists`); }

  // Underwriting
  explain(req: ExplainRequest): Observable<DecisionExplanation> { return this.http.post<DecisionExplanation>(`${this.base}/underwriting/explain`, req); }
  recommendTerms(req: TermsRequest): Observable<TermsRecommendation> { return this.http.post<TermsRecommendation>(`${this.base}/underwriting/recommend-terms`, req); }
  volumePlausibility(req: VolumePlausibilityRequest): Observable<VolumePlausibilityResult> { return this.http.post<VolumePlausibilityResult>(`${this.base}/underwriting/volume-plausibility`, req); }
  bankStatementFile(file: File): Observable<CashFlowAnalysis> { return this.http.post<CashFlowAnalysis>(`${this.base}/underwriting/bank-statement`, this.form(file)); }
  bankStatementCsv(csv: string): Observable<CashFlowAnalysis> { return this.http.post<CashFlowAnalysis>(`${this.base}/underwriting/bank-statement/csv`, { csv }); }
  financialStatementFile(file: File, declaredAnnualCardVolume?: number): Observable<FinancialStatementAnalysis> {
    const form = this.form(file);
    if (declaredAnnualCardVolume != null) form.append('declaredAnnualCardVolume', declaredAnnualCardVolume.toString());
    return this.http.post<FinancialStatementAnalysis>(`${this.base}/underwriting/financial-statement`, form);
  }
  financialStatementText(text: string, declaredAnnualCardVolume?: number): Observable<FinancialStatementAnalysis> {
    return this.http.post<FinancialStatementAnalysis>(`${this.base}/underwriting/financial-statement/text`, { text, declaredAnnualCardVolume });
  }

  // Platform: score & rules
  score(req: UnifiedScoreRequest): Observable<UnifiedScoreResponse> { return this.http.post<UnifiedScoreResponse>(`${this.base}/platform/score`, req); }
  rules(): Observable<RuleSet> { return this.http.get<RuleSet>(`${this.base}/platform/rules`); }
  rulesHistory(): Observable<RuleSetVersion[]> { return this.http.get<RuleSetVersion[]>(`${this.base}/platform/rules/history`); }
  rulesVersion(v: number): Observable<RuleSet> { return this.http.get<RuleSet>(`${this.base}/platform/rules/${v}`); }
  validateRules(ruleSet: RuleSet): Observable<{ valid: boolean; rules: number }> { return this.http.post<{ valid: boolean; rules: number }>(`${this.base}/platform/rules/validate`, ruleSet); }
  publishRules(ruleSet: RuleSet, author: string, comment?: string): Observable<RuleSetVersion> { return this.http.post<RuleSetVersion>(`${this.base}/platform/rules/publish`, { ruleSet, author, comment }); }
  rollbackRules(version: number, actor: string): Observable<RuleSetVersion> { return this.http.post<RuleSetVersion>(`${this.base}/platform/rules/rollback/${version}`, { actor }); }
  evaluateRules(facts: Record<string, unknown>, ruleSet?: RuleSet): Observable<RulesEvaluation> { return this.http.post<RulesEvaluation>(`${this.base}/platform/rules/evaluate`, { facts, ruleSet }); }

  // Platform: cases
  createCase(req: { merchantName: string; externalRef?: string; priority?: CasePriority; riskScore?: number; riskTier?: string; rulesOutcome?: RuleOutcome; actor: string }): Observable<MerchantCase> {
    return this.http.post<MerchantCase>(`${this.base}/platform/cases`, req);
  }
  cases(status?: CaseStatus | null, assignedTo?: string | null): Observable<MerchantCase[]> {
    let params = new HttpParams().set('limit', 200);
    if (status) params = params.set('status', status);
    if (assignedTo) params = params.set('assignedTo', assignedTo);
    return this.http.get<MerchantCase[]>(`${this.base}/platform/cases`, { params });
  }
  caseStats(): Observable<CaseQueueStats> { return this.http.get<CaseQueueStats>(`${this.base}/platform/cases/stats`); }
  case(id: string): Observable<MerchantCase> { return this.http.get<MerchantCase>(`${this.base}/platform/cases/${id}`); }
  assignCase(id: string, assignee: string, actor: string): Observable<MerchantCase> { return this.http.post<MerchantCase>(`${this.base}/platform/cases/${id}/assign`, { assignee, actor }); }
  setCaseStatus(id: string, status: CaseStatus, actor: string, reason?: string): Observable<MerchantCase> { return this.http.post<MerchantCase>(`${this.base}/platform/cases/${id}/status`, { status, actor, reason }); }
  addNote(id: string, author: string, body: string): Observable<CaseNote> { return this.http.post<CaseNote>(`${this.base}/platform/cases/${id}/notes`, { author, body }); }
  notes(id: string): Observable<CaseNote[]> { return this.http.get<CaseNote[]>(`${this.base}/platform/cases/${id}/notes`); }
  decideCase(id: string, decision: 'Approved' | 'Declined', actor: string, reason: string): Observable<MerchantCase> { return this.http.post<MerchantCase>(`${this.base}/platform/cases/${id}/decide`, { decision, actor, reason }); }
  caseAudit(id: string): Observable<AuditEvent[]> { return this.http.get<AuditEvent[]>(`${this.base}/platform/cases/${id}/audit`); }

  // Platform: audit
  audit(limit = 100): Observable<AuditEvent[]> { return this.http.get<AuditEvent[]>(`${this.base}/platform/audit`, { params: { limit } }); }
  verifyAudit(): Observable<AuditVerification> { return this.http.get<AuditVerification>(`${this.base}/platform/audit/verify`); }

  // Platform: webhooks
  webhooks(): Observable<WebhookSubscription[]> { return this.http.get<WebhookSubscription[]>(`${this.base}/platform/webhooks`); }
  webhookEvents(): Observable<string[]> { return this.http.get<string[]>(`${this.base}/platform/webhooks/events`); }
  registerWebhook(url: string, secret: string, events: string[]): Observable<WebhookSubscription> { return this.http.post<WebhookSubscription>(`${this.base}/platform/webhooks`, { url, secret, events }); }
  removeWebhook(id: string): Observable<void> { return this.http.delete<void>(`${this.base}/platform/webhooks/${id}`); }
  deliveries(): Observable<WebhookDelivery[]> { return this.http.get<WebhookDelivery[]>(`${this.base}/platform/webhooks/deliveries`); }

  // Platform: model ops
  models(): Observable<ModelsResponse> { return this.http.get<ModelsResponse>(`${this.base}/platform/models`); }
  decisions(onlyLabelled = false, limit = 100): Observable<LoggedDecision[]> { return this.http.get<LoggedDecision[]>(`${this.base}/platform/models/decisions`, { params: { onlyLabelled, limit } }); }
  recordOutcome(id: number, actual: Decision, actor: string): Observable<unknown> { return this.http.post(`${this.base}/platform/models/decisions/${id}/outcome`, { actual, actor }); }
  drift(): Observable<DriftReport> { return this.http.get<DriftReport>(`${this.base}/platform/models/drift`); }
  compare(): Observable<ChampionChallengerReport> { return this.http.get<ChampionChallengerReport>(`${this.base}/platform/models/compare`); }
  retrain(actor: string, syntheticRows: number, registerAsChallenger: boolean): Observable<RetrainResult> { return this.http.post<RetrainResult>(`${this.base}/platform/models/retrain`, { actor, syntheticRows, registerAsChallenger }); }
  promote(actor: string, justification?: string): Observable<RegisteredModel> { return this.http.post<RegisteredModel>(`${this.base}/platform/models/promote`, { actor, justification }); }

  // Platform: MATCH
  matchInquiry(req: MatchInquiryRequest): Observable<MatchResult> { return this.http.post<MatchResult>(`${this.base}/platform/match/inquiry`, req); }

  private form(file: File): FormData { const f = new FormData(); f.append('file', file, file.name); return f; }

  // Full assessment
  assessmentSteps(): Observable<AssessmentStepDescriptor[]> { return this.http.get<AssessmentStepDescriptor[]>(`${this.base}/assessment/steps`); }
  assessments(limit = 50): Observable<AssessmentListItem[]> { return this.http.get<AssessmentListItem[]>(`${this.base}/assessment`, { params: new HttpParams().set('limit', limit) }); }
  assessment(id: string): Observable<AssessmentResult> { return this.http.get<AssessmentResult>(`${this.base}/assessment/${encodeURIComponent(id)}`); }
  assessmentPdfUrl(id: string): string { return `${this.base}/assessment/${encodeURIComponent(id)}/pdf`; }

  /**
   * Runs the full assessment and emits one event per check as the server streams NDJSON progress, ending with the
   * complete result. Uses fetch because HttpClient buffers the whole response. If the server stops sending for
   * `idleTimeoutMs` (e.g. the API died behind the dev proxy) the run is aborted with an error instead of spinning.
   */
  runAssessment(req: AssessmentRequest, files: { bankStatement?: File | null; financialStatement?: File | null } = {}, idleTimeoutMs = 120_000): Observable<AssessmentEvent> {
    return new Observable<AssessmentEvent>(subscriber => {
      const controller = new AbortController();
      let idleTimer: ReturnType<typeof setTimeout> | undefined;
      let timedOut = false;
      const armWatchdog = () => {
        if (idleTimer) clearTimeout(idleTimer);
        idleTimer = setTimeout(() => { timedOut = true; controller.abort(); }, idleTimeoutMs);
      };
      let body: BodyInit;
      const headers: Record<string, string> = { Accept: 'application/x-ndjson' };
      if (files.bankStatement || files.financialStatement) {
        const form = new FormData();
        form.append('request', JSON.stringify(req));
        if (files.bankStatement) form.append('bankStatement', files.bankStatement, files.bankStatement.name);
        if (files.financialStatement) form.append('financialStatement', files.financialStatement, files.financialStatement.name);
        body = form;
      } else {
        body = JSON.stringify(req);
        headers['Content-Type'] = 'application/json';
      }

      (async () => {
        armWatchdog();
        const res = await fetch(`${this.base}/assessment/run/stream`, { method: 'POST', body, headers, signal: controller.signal });
        if (!res.ok || !res.body) {
          const text = await res.text();
          let message = `${res.status} ${res.statusText}`;
          try {
            const parsed = JSON.parse(text);
            if (parsed?.errors) message = Object.values(parsed.errors as Record<string, string[]>).flat().join(' ');
            else if (parsed?.detail) message = parsed.detail;
            else if (parsed?.title) message = parsed.title;
          } catch { if (text) message = text; }
          throw new Error(message);
        }
        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        let finished = false;
        const emit = (line: string) => {
          const ev = JSON.parse(line) as AssessmentEvent;
          if (ev.type === 'result' || ev.type === 'error') finished = true;
          subscriber.next(ev);
        };
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          armWatchdog();
          buffer += decoder.decode(value, { stream: true });
          let nl: number;
          while ((nl = buffer.indexOf('\n')) >= 0) {
            const line = buffer.slice(0, nl).trim();
            buffer = buffer.slice(nl + 1);
            if (line) emit(line);
          }
        }
        if (buffer.trim()) emit(buffer.trim());
        if (!finished) throw new Error('The API stopped responding before the assessment finished. Check that the API is running and try again.');
        subscriber.complete();
      })().catch(err => {
        if (timedOut) {
          subscriber.error(new Error(`No progress from the API for ${Math.round(idleTimeoutMs / 1000)}s – the run was aborted. Check that the API is running and try again.`));
          return;
        }
        if (controller.signal.aborted) return;
        subscriber.error(err instanceof Error ? err : new Error(String(err)));
      }).finally(() => { if (idleTimer) clearTimeout(idleTimer); });

      return () => { if (idleTimer) clearTimeout(idleTimer); controller.abort(); };
    });
  }
}
