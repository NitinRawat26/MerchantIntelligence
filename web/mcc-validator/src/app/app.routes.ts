import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'assess' },
  { path: 'assess', title: 'Full Assessment', loadComponent: () => import('./features/assessment/assessment.component').then(m => m.AssessmentComponent) },
  { path: 'assess/:id', title: 'Assessment', loadComponent: () => import('./features/assessment/assessment.component').then(m => m.AssessmentComponent) },
  { path: 'score', title: 'Risk Score', loadComponent: () => import('./features/platform/score.component').then(m => m.ScoreComponent) },
  { path: 'kyb', title: 'KYB', loadComponent: () => import('./features/kyb/kyb.component').then(m => m.KybComponent) },
  { path: 'underwriting', title: 'Underwriting', loadComponent: () => import('./features/underwriting/underwriting.component').then(m => m.UnderwritingComponent) },
  { path: 'mcc', title: 'MCC Validator', loadComponent: () => import('./features/mcc/mcc-validator.component').then(m => m.MccValidatorComponent) },
  { path: 'cases', title: 'Cases', loadComponent: () => import('./features/platform/cases.component').then(m => m.CasesComponent) },
  { path: 'cases/:id', title: 'Case', loadComponent: () => import('./features/platform/case-detail.component').then(m => m.CaseDetailComponent) },
  { path: 'rules', title: 'Rules', loadComponent: () => import('./features/platform/rules.component').then(m => m.RulesComponent) },
  { path: 'workflows', title: 'Workflows', loadComponent: () => import('./features/platform/workflows.component').then(m => m.WorkflowsComponent) },
  { path: 'audit', title: 'Audit', loadComponent: () => import('./features/platform/audit.component').then(m => m.AuditComponent) },
  { path: 'models', title: 'Model Ops', loadComponent: () => import('./features/platform/models.component').then(m => m.ModelsComponent) },
  { path: 'match', title: 'MATCH', loadComponent: () => import('./features/platform/match.component').then(m => m.MatchComponent) },
  { path: 'webhooks', title: 'Webhooks', loadComponent: () => import('./features/platform/webhooks.component').then(m => m.WebhooksComponent) },
  { path: '**', redirectTo: 'assess' }
];
