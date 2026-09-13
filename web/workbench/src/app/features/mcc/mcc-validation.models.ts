export type RiskTier = 'Low' | 'Medium' | 'High';
export type MccVerdict = 'Consistent' | 'Questionable' | 'Inconsistent' | 'Insufficient';

export interface MccCatalogItem {
  mcc: number;
  description: string;
  category: string;
  riskTier: RiskTier;
}

export interface MccCandidate {
  mcc: number;
  description: string;
  score: number;
}

export interface RiskFlag {
  code: string;
  message: string;
  severity: RiskTier;
}

export interface ProviderEvidence {
  provider: string;
  succeeded: boolean;
  candidates: MccCandidate[];
  highlights: string[];
  error: string | null;
}

export interface MccValidationRequest {
  mcc: number;
  websiteUrl: string;
}

export interface MccValidationResult {
  declaredMcc: number;
  declaredDescription: string;
  declaredRiskTier: RiskTier;
  websiteUrl: string;
  verdict: MccVerdict;
  accuracyPercent: number;
  suggestedMccs: MccCandidate[];
  riskFlags: RiskFlag[];
  evidence: ProviderEvidence[];
  pagesAnalyzed: string[];
}

export interface HistoryEntry {
  at: Date;
  request: MccValidationRequest;
  result: MccValidationResult;
}
