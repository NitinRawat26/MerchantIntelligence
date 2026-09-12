export type RiskTier = 'Low' | 'Medium' | 'High';
export type Decision = 'Approved' | 'Declined' | 'Cancelled';
export type RuleOutcome = 'Approve' | 'Refer' | 'Decline';
export type BusinessPolicy = 'Acceptable' | 'HighRisk' | 'Restricted' | 'Prohibited';

export interface Flag { code: string; message: string; severity: RiskTier; }

// ---- KYB -------------------------------------------------------------------

export interface BusinessIdentityRequest {
  legalName: string; tradingName?: string; registrationNumber?: string; taxId?: string;
  addressLine?: string; city?: string; region?: string; postalCode?: string; country?: string; websiteUrl?: string;
}
export interface BeneficialOwnerRequest { fullName: string; dateOfBirth?: string; nationality?: string; role?: string; ownershipPercent?: number; }
export interface FullKybRequest { business: BusinessIdentityRequest; owners: BeneficialOwnerRequest[]; businessDescription?: string; declaredMcc?: number; }

export interface RegistryRecord {
  source: string; sourceId: string; legalName: string; status?: string | null; incorporationDate?: string | null; jurisdiction?: string | null;
  registrationNumber?: string | null; address?: string | null; entityType?: string | null; sourceUrl?: string | null; extra?: Record<string, string> | null;
}
export interface RegistryMatch { record: RegistryRecord; nameScore: number; addressScore: number; overallScore: number; }
export interface RegistrySourceResult { source: string; succeeded: boolean; error?: string | null; matches: RegistryMatch[]; }
export interface BusinessVerificationResult {
  status: string; confidencePercent: number; bestMatch?: RegistryMatch | null; entityAgeMonths?: number | null;
  address?: { provider: string; verified: boolean; [k: string]: unknown } | null; sources: RegistrySourceResult[]; flags: Flag[];
}
export interface SanctionsHit {
  entity: { id: string; listName: string; type: string; name: string; aliases: string[]; countries: string[]; programs: string[]; remarks?: string | null; sourceUrl?: string | null };
  matchedName: string; nameScore: number; score: number; reasons: string[];
}
export interface SubjectScreeningResult {
  subject: { name: string; isIndividual: boolean; role?: string }; potentialMatch: boolean; hits: SanctionsHit[];
  adverseMedia?: { provider: string; succeeded: boolean; articleCount: number; negativeCount: number; articles: { title: string; url: string; source: string; published?: string | null; tone: string }[]; error?: string | null } | null;
  flags: Flag[];
}
export interface ScreeningReport {
  subjects: SubjectScreeningResult[]; lists: { listName: string; entityCount: number; loadedAt?: string; error?: string }[];
  overallRisk: RiskTier; flags: Flag[];
}
export interface ComplianceCheck { code: string; title: string; status: 'Pass' | 'Warn' | 'Fail' | 'Skipped'; detail: string; severity: RiskTier; evidence?: string | null; }
export interface ProhibitedBusinessResult {
  verdict: BusinessPolicy;
  matches: { category: { code: string; name: string; policy: BusinessPolicy; mccs: number[]; keywords: string[]; notes: string }; score: number; matchedKeywords: string[]; declaredMccInCategory: boolean }[];
  flags: Flag[];
}
export interface WebsiteComplianceResult {
  websiteUrl: string; reachable: boolean; score: number; grade: string; checks: ComplianceCheck[];
  domain?: { domain: string; registered?: string | null; expires?: string | null; registrar?: string | null; statuses: string[]; error?: string | null } | null;
  prohibitedBusiness: ProhibitedBusinessResult; pagesAnalyzed: string[];
}
export interface KybReport { verification: BusinessVerificationResult; screening: ScreeningReport; websiteCompliance?: WebsiteComplianceResult | null; overallRisk: RiskTier; flags: Flag[]; }

// ---- Credit / underwriting ----------------------------------------------------

export interface CreditDecisionRequest {
  merchantCategoryCode: number; annualVolume: number; averageTicket: number; highestTicket: number; matchFound: boolean; existingRelationship: boolean;
}
export interface DecisionResult { decision: Decision; confidence: number; probabilities: Record<Decision, number>; }
export interface ExplainRequest extends CreditDecisionRequest { explainClass?: Decision | null; }
export interface DecisionExplanation {
  decision: Decision; confidence: number; explainedClass: Decision; baselineProbability: number; predictedProbability: number;
  contributions: { feature: string; value: string; baselineValue: string; contribution: number; direction: string }[];
  reasonCodes: { code: string; description: string; weight: number }[]; narrative: string;
}
export interface TermsRequest extends CreditDecisionRequest {
  deliveryDays?: number | null; cardNotPresentShare: number; kybHighRisk?: boolean | null; websiteComplianceScore?: number | null;
  volumePlausibilityScore?: number | null; offersSubscriptions: boolean; offersFreeTrials: boolean;
}
export interface TermsRecommendation {
  riskBand: string; riskScore: number;
  reserve: { type: string; rollingPercent: number; rollingDays: number; capAmount: number; upfrontAmount: number; estimatedSteadyStateBalance: number };
  pricing: { interchangePlusMarkupBps: number; perTransactionFee: number; monthlyFee: number; chargebackFee: number; settlementDelayDays: number; monthlyVolumeCap: number; singleTransactionCap: number };
  estimatedExposure: number; factors: { code: string; description: string; effect: string }[]; benchmarkSource: string;
}
export interface VolumePlausibilityRequest {
  annualVolume: number; averageTicket: number; highestTicket?: number | null; merchantCategoryCode?: number | null; employeeCount?: number | null;
  yearsInBusiness?: number | null; priorYearRevenue?: number | null; monthlyCardVolumeFromStatements?: number | null; websiteProductCount?: number | null; hasPhysicalLocation?: boolean | null;
  locationCount?: number | null;
}
export interface VolumePlausibilityResult {
  plausibilityScore: number; verdict: string; metrics: { name: string; value: string; benchmark: string; assessment: string }[]; flags: Flag[]; benchmarkSource: string;
}
export interface CashFlowAnalysis {
  periodStart: string; periodEnd: string; monthsCovered: number; transactionCount: number; totalInflows: number; totalOutflows: number;
  averageMonthlyInflows: number; averageMonthlyOutflows: number; averageMonthlyNet: number; averageMonthlyCardDeposits: number; impliedAnnualCardVolume: number;
  averageDailyBalance?: number | null; minimumBalance?: number | null; negativeBalanceDays: number; nsfOrOverdraftCount: number; returnedItemCount: number;
  loanRepayments: number; payroll: number; ownerDraws: number; largestSingleDeposit: number; inflowVolatility: number; seasonalityIndex: number; detectedProcessors: string[];
  monthly: { month: string; inflows: number; outflows: number; net: number; cardProcessorDeposits: number; transactionCount: number; endingBalance?: number | null }[];
  flags: Flag[]; warnings: string[];
}
export interface FinancialStatementAnalysis {
  statement: Record<string, number | null | undefined>; ratios: { name: string; value?: number | null; benchmark: string; assessment: string }[]; flags: Flag[]; warnings: string[];
}

// ---- Platform ----------------------------------------------------------------

export interface UnifiedScoreRequest {
  application?: CreditDecisionRequest | null; kybRisk?: RiskTier | null; businessVerified?: boolean | null; entityAgeMonths?: number | null;
  sanctionsMatch?: boolean | null; pepMatch?: boolean | null; adverseMedia?: boolean | null; prohibitedVerdict?: BusinessPolicy | null;
  websiteComplianceScore?: number | null; volumePlausibilityScore?: number | null; termsRiskBand?: string | null; matchFound?: boolean | null;
  createCase: boolean; merchantName?: string | null; externalRef?: string | null; actor: string;
}
export interface UnifiedRiskScore {
  score: number; tier: string; recommendedAction: RuleOutcome;
  components: { name: string; weight: number; score: number; weighted: number; detail: string; covered: boolean }[];
  reasonCodes: { code: string; description: string; severity: RiskTier; source: string }[];
  coverageGaps: string[]; hardStops: string[]; coveragePercent: number;
}
export interface RulesEvaluation { outcome: RuleOutcome; matchedRules: { id: string; outcome: RuleOutcome; priority: number; description?: string }[]; decidingRule: string; ruleSetVersion: string; facts: Record<string, unknown>; }
export interface UnifiedScoreResponse { score: UnifiedRiskScore; rules: RulesEvaluation; creditDecision?: DecisionResult | null; decisionLogId?: number | null; case?: MerchantCase | null; }

export interface RuleSet { version?: string; defaultOutcome?: RuleOutcome; rules: unknown[]; [k: string]: unknown; }
export interface RuleSetVersion { version: number; author: string; comment?: string; createdAt: string; active: boolean; ruleCount: number; }

export type CaseStatus = 'Open' | 'InReview' | 'PendingDocuments' | 'Approved' | 'Declined' | 'Withdrawn';
export type CasePriority = 'Low' | 'Normal' | 'High' | 'Urgent';
export interface MerchantCase {
  id: string; merchantName: string; externalRef?: string | null; status: CaseStatus; assignedTo?: string | null; priority: CasePriority;
  riskScore?: number | null; riskTier?: string | null; rulesOutcome?: RuleOutcome | null; finalDecision?: string | null; snapshot?: unknown; createdAt: string; updatedAt: string;
}
export interface CaseNote { id: number; caseId: string; author: string; body: string; createdAt: string; }
export interface CaseQueueStats { open: number; inReview: number; pendingDocuments: number; approved: number; declined: number; withdrawn: number; overrides: number; averageOpenAgeHours: number; }
export interface AuditEvent { seq: number; caseId?: string | null; actor: string; action: string; detail?: unknown; occurredAt: string; previousHash: string; hash: string; }
export interface AuditVerification { valid: boolean; eventsChecked: number; firstBrokenSeq?: number | null; }

export interface WebhookSubscription { id: string; url: string; events: string[]; enabled: boolean; createdAt: string; }
export interface WebhookDelivery { id: number; webhookId: string; event: string; attempts: number; statusCode?: number | null; error?: string | null; delivered: boolean; createdAt: string; lastAttemptAt?: string | null; }

export interface RegisteredModel { version: string; path: string; role: 'Champion' | 'Challenger' | 'Retired'; metrics?: Record<string, unknown> | null; trainingRows?: number | null; registeredAt: string; }
export interface ModelsResponse { champion: string; challenger?: string | null; registry: RegisteredModel[]; }
export interface LoggedDecision { id: number; caseId?: string | null; modelVersion: string; application: CreditDecisionRequest; predicted: Decision; confidence: number; challengerPredicted?: Decision | null; actual?: Decision | null; scoredAt: string; }
export interface DriftReport { referenceRows: number; recentRows: number; since?: string | null; features: { feature: string; psi: number; status: string; referenceShare: number[]; recentShare: number[] }[]; predictionDriftPsi: number; overallStatus: string; alerts: string[]; }
export interface ModelPerformance { version: string; scored: number; withOutcome: number; accuracy?: number | null; approvalPrecision?: number | null; declineRecall?: number | null; confusion: Record<string, number>; }
export interface ChampionChallengerReport { champion: ModelPerformance; challenger?: ModelPerformance | null; disagreements: number; recommendation: string; }
export interface RetrainResult { version: string; path: string; metrics: Record<string, unknown>; labelledRows: number; syntheticRows: number; registeredAsChallenger: boolean; }

export interface MatchInquiryRequest { legalName: string; doingBusinessAs?: string; taxId?: string; country?: string; addressLine?: string; city?: string; region?: string; postalCode?: string; principals: { firstName: string; lastName: string; dateOfBirth?: string; nationalId?: string }[]; }
export interface MatchResult { availability: 'NotConfigured' | 'Available' | 'Error'; found?: boolean | null; hits: { matchedOn: string; reasonCode: string; reasonDescription: string; terminationDate?: string; acquirer?: string }[]; provider: string; message?: string | null; }

// ---- Full assessment -----------------------------------------------------------

export interface AssessmentRequest {
  business: BusinessIdentityRequest; owners: BeneficialOwnerRequest[]; businessDescription?: string;
  merchantCategoryCode: number; annualVolume: number; averageTicket: number; highestTicket: number; existingRelationship: boolean;
  deliveryDays?: number | null; cardNotPresentShare: number; offersSubscriptions: boolean; offersFreeTrials: boolean;
  employeeCount?: number | null; yearsInBusiness?: number | null; priorYearRevenue?: number | null; websiteProductCount?: number | null; hasPhysicalLocation?: boolean | null;
  locationCount?: number | null;
  bankStatementCsv?: string | null; financialStatementText?: string | null; externalRef?: string | null; actor: string; createCase: boolean;
}
export type StepStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped';
export interface AssessmentStepDescriptor { id: string; name: string; enabled?: boolean; }

// ---- Workflows ------------------------------------------------------------------------
export type StepFailurePolicy = 'Skip' | 'Refer' | 'Abort';
export interface WorkflowStepConfig { id: string; enabled: boolean; onFail: StepFailurePolicy; dependsOn?: string[] | null; params?: Record<string, unknown> | null; }
export interface WorkflowDefinition { name: string; version: string; description?: string | null; haltOnHardStop: boolean; steps: WorkflowStepConfig[]; }
export interface WorkflowVersion { version: number; name: string; author: string; comment?: string | null; createdAt: string; active: boolean; enabledSteps: number; totalSteps: number; }
export interface WorkflowParamDescriptor { name: string; type: string; default: string; description: string; }
export interface WorkflowStepDescriptor { id: string; name: string; description: string; dependsOn: string[]; consumes: string[]; required: boolean; params: WorkflowParamDescriptor[]; }
export interface WorkflowStage { index: number; steps: string[]; }
export interface WorkflowPlan { stages: WorkflowStage[]; warnings: string[]; disabled: string[]; mermaid: string; }
export interface WorkflowValidationResponse { valid: boolean; error?: string | null; plan?: WorkflowPlan | null; }
export interface AssessmentStep { id: string; name: string; status: StepStatus; summary: string; durationMs: number; error?: string | null; }
export interface CheckOutcome { check: string; result: string; detail: string; severity: RiskTier; covered: boolean; }
export interface ExplanationItem { section: string; code: string; message: string; severity: RiskTier; source: string; }
export interface AssessmentExplainability {
  headline: string; narrative: string[]; checkOutcomes: CheckOutcome[]; findings: ExplanationItem[];
  scoreComponents: UnifiedRiskScore['components']; reasonCodes: UnifiedRiskScore['reasonCodes']; creditContributions: DecisionExplanation['contributions'];
  matchedRules: RulesEvaluation['matchedRules']; decidingRule?: string | null; coverageGaps: string[]; hardStops: string[]; analystNextSteps: string[];
}
export interface AssessmentDecision { outcome: RuleOutcome; score: number; tier: string; coveragePercent: number; ruleSetVersion: string; summary: string; }
export interface AssessmentIntakeSummary {
  business: BusinessIdentityRequest & { fullAddress?: string }; owners: BeneficialOwnerRequest[]; businessDescription?: string | null; merchantCategoryCode: number;
  annualVolume: number; averageTicket: number; highestTicket: number; existingRelationship: boolean; deliveryDays?: number | null; cardNotPresentShare: number;
  offersSubscriptions: boolean; offersFreeTrials: boolean; employeeCount?: number | null; yearsInBusiness?: number | null; priorYearRevenue?: number | null;
  websiteProductCount?: number | null; hasPhysicalLocation?: boolean | null; locationCount?: number | null; bankStatementSource?: string | null; financialStatementSource?: string | null; externalRef?: string | null; actor: string;
}
export interface MccValidationSummary {
  declaredMcc: number; declaredDescription: string; declaredRiskTier: RiskTier; websiteUrl: string; verdict: 'Consistent' | 'Questionable' | 'Inconsistent' | 'Insufficient';
  accuracyPercent: number; suggestedMccs: { mcc: number; description: string; score: number; riskTier: RiskTier; matchedKeywords?: string[] }[];
  riskFlags: Flag[]; evidence: { provider: string; succeeded: boolean; error?: string | null; [k: string]: unknown }[]; pagesAnalyzed: string[];
}
export interface AssessmentResult {
  id: string; startedAt: string; completedAt: string; intake: AssessmentIntakeSummary; steps: AssessmentStep[];
  decision: AssessmentDecision; explainability: AssessmentExplainability;
  verification?: BusinessVerificationResult | null; screening?: ScreeningReport | null; websiteCompliance?: WebsiteComplianceResult | null;
  prohibitedBusiness?: ProhibitedBusinessResult | null; mccValidation?: MccValidationSummary | null; match?: MatchResult | null;
  bankStatement?: CashFlowAnalysis | null; financialStatement?: FinancialStatementAnalysis | null; volumePlausibility?: VolumePlausibilityResult | null;
  creditDecision?: DecisionResult | null; creditExplanation?: DecisionExplanation | null; terms?: TermsRecommendation | null;
  unifiedScore?: UnifiedRiskScore | null; rules?: RulesEvaluation | null; case?: MerchantCase | null; decisionLogId?: number | null;
}
export interface AssessmentListItem { id: string; merchantName: string; outcome: RuleOutcome; score: number; tier: string; coveragePercent: number; caseId?: string | null; completedAt: string; }
export type AssessmentEvent =
  | { type: 'steps'; steps: AssessmentStepDescriptor[] }
  | { type: 'step'; step: AssessmentStep }
  | { type: 'result'; result: AssessmentResult }
  | { type: 'error'; error: string }
  | { type: 'heartbeat'; at: string };
