import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { RuleSet, RuleSetVersion, RulesEvaluation } from '../../shared/models';
import { FieldHintComponent, StatusComponent, outcomeClass } from '../../shared/ui';

const SAMPLE_FACTS = {
  score: 720, tier: 'Low', coveragePercent: 85, sanctionsMatch: false, prohibitedVerdict: 'Acceptable', matchFound: false,
  pepMatch: false, highSeverityReasons: 0, hardStops: [], annualVolume: 480000, highestTicket: 1200, entityAgeMonths: 84, creditDecision: 'Approved'
};

@Component({
  selector: 'mi-rules',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatTableModule, MatTabsModule, StatusComponent, FieldHintComponent],
  styleUrl: './platform.scss',
  template: `
    <div class="page">
      <mat-card appearance="outlined">
        <mat-card-header>
          <mat-icon mat-card-avatar>rule</mat-icon>
          <mat-card-title>Policy rules</mat-card-title>
          <mat-card-subtitle>Versioned JSON rule set evaluated alongside the ML score · Decline &gt; Refer &gt; Approve</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <mi-status [loading]="loading()" [error]="error()"></mi-status>
          <mat-tab-group>
            <mat-tab label="Active rule set">
              <div class="tab">
                <div class="row">
                  <button mat-stroked-button type="button" (click)="load()"><mat-icon>refresh</mat-icon> Reload</button>
                  <button mat-stroked-button type="button" (click)="validate()" [disabled]="!editor.valid"><mat-icon>check_circle</mat-icon> Validate</button>
                  <mat-form-field appearance="outline" class="w200"><mat-label>Author</mat-label><input matInput [formControl]="author"><mi-field-hint matSuffix for="ruleAuthor"></mi-field-hint></mat-form-field>
                  <mat-form-field appearance="outline" class="w300"><mat-label>Comment</mat-label><input matInput [formControl]="comment"><mi-field-hint matSuffix for="ruleComment"></mi-field-hint></mat-form-field>
                  <button mat-flat-button color="primary" type="button" (click)="publish()" [disabled]="!editor.valid || author.invalid"><mat-icon>publish</mat-icon> Publish new version</button>
                </div>
                @if (message(); as m) { <p [class]="m.ok ? 'text-low' : 'text-high'">{{ m.text }}</p> }
                <mat-form-field appearance="outline" class="full">
                  <mat-label>Rule set JSON</mat-label>
                  <textarea matInput class="mono" rows="24" [formControl]="editor"></textarea>
                  @if (editor.invalid) { <mat-error>Not valid JSON</mat-error> }
                <mi-field-hint matSuffix for="ruleEditor"></mi-field-hint></mat-form-field>
              </div>
            </mat-tab>

            <mat-tab label="Evaluate facts">
              <div class="tab two-col">
                <div>
                  <p class="muted">Facts are the flattened view of a scoring result (score, tier, sanctionsMatch, annualVolume…). Evaluates against the JSON in the editor tab if it parses, else the active set.</p>
                  <mat-form-field appearance="outline" class="full">
                    <mat-label>Facts JSON</mat-label>
                    <textarea matInput class="mono" rows="14" [formControl]="facts"></textarea>
                  <mi-field-hint matSuffix for="ruleFacts"></mi-field-hint></mat-form-field>
                  <button mat-flat-button color="primary" type="button" (click)="evaluate()" [disabled]="facts.invalid"><mat-icon>play_arrow</mat-icon> Evaluate</button>
                </div>
                <div>
                  @if (evaluation(); as ev) {
                    <div class="headline"><span class="pill" [class]="outcomeClass(ev.outcome)">{{ ev.outcome }}</span> <span class="muted">deciding rule <code>{{ ev.decidingRule }}</code> · v{{ ev.ruleSetVersion }}</span></div>
                    <ul class="rule-list">
                      @for (m of ev.matchedRules; track m.id) {
                        <li><span class="pill small" [class]="outcomeClass(m.outcome)">{{ m.outcome }}</span> <code>{{ m.id }}</code> <span class="muted">p{{ m.priority }}</span> {{ m.description }}</li>
                      } @empty { <li class="muted">No rules matched — default outcome.</li> }
                    </ul>
                  } @else { <p class="empty">Run an evaluation to see matched rules.</p> }
                </div>
              </div>
            </mat-tab>

            <mat-tab label="Version history">
              <div class="tab">
                <table mat-table [dataSource]="history()" class="full">
                  <ng-container matColumnDef="version"><th mat-header-cell *matHeaderCellDef>Version</th><td mat-cell *matCellDef="let h">v{{ h.version }} @if (h.active) {<span class="pill small good">active</span>}</td></ng-container>
                  <ng-container matColumnDef="author"><th mat-header-cell *matHeaderCellDef>Author</th><td mat-cell *matCellDef="let h">{{ h.author }}</td></ng-container>
                  <ng-container matColumnDef="comment"><th mat-header-cell *matHeaderCellDef>Comment</th><td mat-cell *matCellDef="let h">{{ h.comment }}</td></ng-container>
                  <ng-container matColumnDef="rules"><th mat-header-cell *matHeaderCellDef>Rules</th><td mat-cell *matCellDef="let h">{{ h.ruleCount }}</td></ng-container>
                  <ng-container matColumnDef="created"><th mat-header-cell *matHeaderCellDef>Created</th><td mat-cell *matCellDef="let h">{{ h.createdAt | date:'short' }}</td></ng-container>
                  <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th><td mat-cell *matCellDef="let h">
                    <button mat-button type="button" (click)="view(h.version)">View</button>
                    @if (!h.active) { <button mat-button type="button" color="warn" (click)="rollback(h.version)">Roll back</button> }
                  </td></ng-container>
                  <tr mat-header-row *matHeaderRowDef="historyColumns"></tr>
                  <tr mat-row *matRowDef="let row; columns: historyColumns"></tr>
                </table>
                @if (!history().length) { <p class="empty">Only the built-in default rule set exists; publish to create version history.</p> }
              </div>
            </mat-tab>
          </mat-tab-group>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`.tab { padding: 18px 0 4px; } .w200 { width: 200px; } .w300 { width: 300px; }`]
})
export class RulesComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  readonly outcomeClass = outcomeClass;
  readonly historyColumns = ['version', 'author', 'comment', 'rules', 'created', 'actions'];

  private readonly jsonValidator = (c: { value: string }) => { try { JSON.parse(c.value); return null; } catch { return { json: true }; } };
  readonly editor = this.fb.nonNullable.control('', this.jsonValidator);
  readonly facts = this.fb.nonNullable.control(JSON.stringify(SAMPLE_FACTS, null, 2), this.jsonValidator);
  readonly author = this.fb.nonNullable.control('policy-admin', Validators.required);
  readonly comment = this.fb.nonNullable.control('');

  readonly history = signal<RuleSetVersion[]>([]);
  readonly evaluation = signal<RulesEvaluation | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<{ ok: boolean; text: string } | null>(null);

  constructor() { this.load(); }

  load(): void {
    this.loading.set(true); this.error.set(null); this.message.set(null);
    this.api.rules().subscribe({
      next: r => { this.editor.setValue(JSON.stringify(r, null, 2)); this.loading.set(false); },
      error: e => { this.error.set(describeError(e)); this.loading.set(false); }
    });
    this.api.rulesHistory().subscribe({ next: h => this.history.set(h), error: () => {} });
  }

  private parsed(): RuleSet | null { try { return JSON.parse(this.editor.value) as RuleSet; } catch { return null; } }

  validate(): void {
    const rs = this.parsed(); if (!rs) return;
    this.api.validateRules(rs).subscribe({
      next: r => this.message.set({ ok: r.valid, text: `Valid · ${r.rules} rules` }),
      error: e => this.message.set({ ok: false, text: describeError(e) })
    });
  }

  publish(): void {
    const rs = this.parsed(); if (!rs) return;
    this.api.publishRules(rs, this.author.value, this.comment.value || undefined).subscribe({
      next: v => { this.message.set({ ok: true, text: `Published v${v.version}` }); this.load(); },
      error: e => this.message.set({ ok: false, text: describeError(e) })
    });
  }

  view(version: number): void {
    this.api.rulesVersion(version).subscribe({ next: r => { this.editor.setValue(JSON.stringify(r, null, 2)); this.message.set({ ok: true, text: `Loaded v${version} into editor (not active)` }); }, error: e => this.error.set(describeError(e)) });
  }

  rollback(version: number): void {
    this.api.rollbackRules(version, this.author.value).subscribe({
      next: v => { this.message.set({ ok: true, text: `Rolled back → active v${v.version}` }); this.load(); },
      error: e => this.message.set({ ok: false, text: describeError(e) })
    });
  }

  evaluate(): void {
    let facts: Record<string, unknown>; try { facts = JSON.parse(this.facts.value); } catch { return; }
    this.error.set(null);
    this.api.evaluateRules(facts, this.parsed() ?? undefined).subscribe({ next: ev => this.evaluation.set(ev), error: e => this.error.set(describeError(e)) });
  }
}
