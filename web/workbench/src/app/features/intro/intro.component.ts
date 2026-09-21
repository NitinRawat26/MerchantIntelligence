import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, inject, output, signal, viewChild } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';

/** Scenes of the intro film; each scene owns a set of positions the SVG elements transition to. */
type Scene = 'cup' | 'signals' | 'steps' | 'agents' | 'pipeline' | 'memo' | 'score';
const SCENES: { name: Scene; ms: number }[] = [
  { name: 'cup', ms: 3600 },
  { name: 'signals', ms: 3800 },
  { name: 'steps', ms: 3600 },
  { name: 'agents', ms: 3200 },
  { name: 'pipeline', ms: 5600 },
  { name: 'memo', ms: 16400 },
  { name: 'score', ms: 0 }
];

/** Camera path over the memo page: section anchor, zoom factor and dwell time. */
const MEMO_SHOTS: { target: string; zoom: number; ms: number }[] = [
  { target: 'm-top', zoom: 1, ms: 2200 },
  { target: 'm-presence', zoom: 1.4, ms: 2800 },
  { target: 'm-website', zoom: 1.4, ms: 2400 },
  { target: 'm-credit', zoom: 1.35, ms: 3000 },
  { target: 'm-score', zoom: 1.3, ms: 2600 },
  { target: 'm-rule', zoom: 1.4, ms: 2200 },
  { target: 'm-bottom', zoom: 1, ms: 1200 }
];

interface MemoCheck { check: string; result: string; detail: string; sev: 'Low' | 'Medium'; id?: string; }
const MEMO_CHECKS: MemoCheck[] = [
  { check: 'Merchant profile', result: 'Mid · LLC', detail: 'Multi-member LLC declared; $900k annual volume → Small, 50 employees lift the segment to Mid. Registry scope Local (state register authoritative); every evidence check applicable, standard weight table.', sev: 'Low' },
  { check: 'Business identity', result: 'Match (94%)', detail: "Best match 'NITIN COFFEE CO LLC' from Texas Secretary of State (name 100 %, address 88 %); entity active since 2011.", sev: 'Low' },
  { id: 'm-presence', check: 'Local presence', result: 'Confirmed (96%)', detail: "'Nitin Coffee Co' via OpenStreetMap (Overpass), 38 m from the declared address (cafe / coffee_shop); name match 100 %, category consistent with MCC 5814.", sev: 'Low' },
  { check: 'Sanctions / PEP / media', result: 'Lists clear', detail: 'All 2 subject(s) clear against 3 loaded list(s) (OpenSanctions/sanctions, OFAC SDN, UN Security Council); no adverse media.', sev: 'Low' },
  { id: 'm-website', check: 'Website compliance', result: 'Grade A', detail: 'Grade A (91/100) over 7 page(s). All card-brand disclosure checks passed. Domain registered 2010-04-12; TLS valid; refund, privacy and contact pages present.', sev: 'Low' },
  { check: 'Prohibited / restricted', result: 'Acceptable', detail: 'No prohibited or restricted category detected (highest score 0.04).', sev: 'Low' },
  { check: 'MCC validation', result: 'Consistent', detail: 'Declared MCC 5814 (Fast Food Restaurants, Low risk) is Consistent with website evidence at 84% agreement.', sev: 'Low' },
  { check: 'MATCH / TMF', result: 'Clear', detail: 'No MATCH record for the entity or its principals.', sev: 'Low' },
  { check: 'Bank statement', result: '6m · 0 flag(s)', detail: '6 month(s) 2025-01-01–2025-06-30, 92 transactions. Average monthly inflows $78,400, card deposits $74,900; 0 NSF, 0 returned items.', sev: 'Low' },
  { check: 'P&L / balance sheet', result: '0 flag(s)', detail: 'Revenue $1,000,000, net income $118,000. Gross margin 0.62 (Healthy), Net margin 0.12 (Healthy), Current ratio 3.3.', sev: 'Low' },
  { check: 'Volume plausibility', result: '100/100', detail: 'Plausible (100/100). Implied transactions / day: 61.6 – Normal. Average ticket $40 within 6–40 benchmark for MCC 5814.', sev: 'Low' },
  { check: 'Credit model', result: 'Approved (96 %)', detail: 'Champion model predicts Approved with 96 % confidence (approve probability 96 %; baseline 93 %).', sev: 'Low' },
  { check: 'Recommended terms', result: 'Band A', detail: 'Risk band A (composite 0.00). Reserve: none. Pricing: IC+ 20 bps + $0.10/txn, T+1 settlement.', sev: 'Low' }
];
const MEMO_SCORE: [string, string, string, string, string][] = [
  ['Identity & KYB', '25%', '88/100', '22.0', 'Registry match 94 %, presence confirmed, owners verified'],
  ['Screening', '15%', '100/100', '15.0', 'No sanctions, PEP, media or MATCH hits'],
  ['Web & category', '15%', '86/100', '12.9', 'Grade A website, MCC consistent, nothing restricted'],
  ['Financial health', '20%', '82/100', '16.4', 'Healthy margins, stable card deposits, no NSF'],
  ['Credit model', '15%', '96/100', '14.4', 'P(Approve) 96 %, above 93 % baseline'],
  ['Plausibility & profile', '10%', '82/100', '8.2', 'Volume plausible; single location, modest headcount']
];
const MEMO_SHAP: [string, string, string, string, string][] = [
  ['ExistingRelationship', 'True', 'False', '+3.9%', 'Increases'],
  ['YearsInBusiness', '15', '6', '+1.6%', 'Increases'],
  ['AnnualVolume', '900,000', '500,000', '-0.9%', 'Decreases'],
  ['AverageTicket', '40.00', '60.00', '+0.2%', 'Neutral'],
  ['MerchantCategoryCode', '5814', '5812', '0.0%', 'Neutral'],
  ['MatchFound', 'False', 'False', '0.0%', 'Neutral']
];

interface Pt { x: number; y: number; }
interface AgentDef { id: string; name: string; mandate: string; steps: string[]; x: number; y: number; w: number; h: number; layout: 'row' | 'column'; }
interface StepDef { id: string; label: string; agent: string; }
interface SignalDef { id: string; label: string; step: string; }

const AGENTS: AgentDef[] = [
  // Profile runs first and alone; Pre-check, KYB and Financial then start together; Decision waits for all three.
  { id: 'profile', name: 'Profile agent', mandate: 'Entity · segment · plan', steps: ['entity', 'segment'], x: -85, y: 150, w: 200, h: 400, layout: 'column' },
  { id: 'precheck', name: 'Pre-check agent', mandate: 'Website · prohibited · MCC', steps: ['website', 'prohibited', 'mcc'], x: 135, y: 150, w: 850, h: 118, layout: 'row' },
  { id: 'kyb', name: 'KYB agent', mandate: 'Identity · presence · screening · MATCH', steps: ['verification', 'presence', 'screening', 'match'], x: 135, y: 291, w: 850, h: 118, layout: 'row' },
  { id: 'financial', name: 'Financial agent', mandate: 'Bank · P&L · plausibility · credit', steps: ['bank', 'financials', 'plausibility', 'credit'], x: 135, y: 432, w: 850, h: 118, layout: 'row' },
  { id: 'decision', name: 'Decision agent', mandate: 'Terms · score · case', steps: ['terms', 'score', 'case'], x: 1055, y: 150, w: 230, h: 400, layout: 'column' }
];
const PROFILE = 0, LANES = [1, 2, 3], DECISION = 4;

const STEPS: StepDef[] = [
  { id: 'entity', label: 'Entity type', agent: 'profile' },
  { id: 'segment', label: 'Segment & plan', agent: 'profile' },
  { id: 'website', label: 'Website compliance', agent: 'precheck' },
  { id: 'prohibited', label: 'Prohibited business', agent: 'precheck' },
  { id: 'mcc', label: 'MCC validation', agent: 'precheck' },
  { id: 'verification', label: 'Identity verification', agent: 'kyb' },
  { id: 'screening', label: 'Sanctions & PEP', agent: 'kyb' },
  { id: 'match', label: 'MATCH inquiry', agent: 'kyb' },
  { id: 'presence', label: 'Local presence', agent: 'kyb' },
  { id: 'bank', label: 'Bank statement', agent: 'financial' },
  { id: 'financials', label: 'P&L & balance sheet', agent: 'financial' },
  { id: 'plausibility', label: 'Volume plausibility', agent: 'financial' },
  { id: 'credit', label: 'Credit model', agent: 'financial' },
  { id: 'terms', label: 'Reserve & pricing', agent: 'decision' },
  { id: 'score', label: 'Unified risk score', agent: 'decision' },
  { id: 'case', label: 'Case & audit', agent: 'decision' }
];

const SIGNALS: SignalDef[] = [
  { id: 'ent', label: 'Entity · LLC', step: 'entity' },
  { id: 'loc', label: '1 location', step: 'segment' },
  { id: 'url', label: 'nitincoffee.co', step: 'website' },
  { id: 'desc', label: 'Independent coffeehouse', step: 'prohibited' },
  { id: 'mcc', label: 'MCC 5814', step: 'mcc' },
  { id: 'legal', label: 'Nitin Coffee Co', step: 'verification' },
  { id: 'owner', label: 'Owner · Nitin Rawat', step: 'screening' },
  { id: 'rel', label: 'Existing relationship', step: 'match' },
  { id: 'addr', label: 'Austin, TX 78701', step: 'presence' },
  { id: 'bank', label: 'Bank statement CSV', step: 'bank' },
  { id: 'pl', label: 'P&L statement', step: 'financials' },
  { id: 'emp', label: '50 employees · 15 yrs', step: 'plausibility' },
  { id: 'vol', label: '$900k annual volume', step: 'credit' },
  { id: 'ticket', label: 'Avg ticket $40', step: 'credit' },
  { id: 'cnp', label: 'CNP share 30%', step: 'terms' },
  { id: 'deliv', label: 'Delivery 0 days', step: 'terms' },
  { id: 'case', label: 'Analyst review', step: 'case' }
];

/** Dependency edges rendered as the pipeline (same shape the workflow runner enforces). */
const EDGES: [string, string][] = [
  ['entity', 'segment'],
  ['website', 'prohibited'],
  ['verification', 'presence'],
  ['bank', 'plausibility'], ['financials', 'plausibility'], ['plausibility', 'credit'], ['match', 'credit'],
  ['terms', 'score'], ['score', 'case']
];

/** Illustrative outcome for a long-standing, low-risk card-present coffeehouse. */
const VERDICT = { score: 889, tier: 'VeryLow', outcome: 'Approve', coveragePercent: 100 };

const STEP_ORDER = ['entity', 'segment', 'website', 'prohibited', 'mcc', 'verification', 'screening', 'match', 'presence', 'bank', 'financials', 'plausibility', 'credit', 'terms', 'score', 'case'];

@Component({
  selector: 'app-intro',
  standalone: true,
  imports: [MatIconModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="film" [attr.data-scene]="scene()">
      <div class="stars"></div>

      <header class="hud">
        <span class="brand"><mat-icon>insights</mat-icon>Merchant Intelligence</span>
        <span class="caption">{{ caption() }}</span>
        <button mat-stroked-button class="skip" (click)="finish()"><mat-icon>skip_next</mat-icon>Skip intro</button>
      </header>

      <svg class="stage" viewBox="-100 0 1400 700" preserveAspectRatio="xMidYMid meet" aria-hidden="true">
        <defs>
          <linearGradient id="agentFill" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stop-color="#1e293b" stop-opacity=".95"/><stop offset="1" stop-color="#0f172a" stop-opacity=".95"/>
          </linearGradient>
          <radialGradient id="glow"><stop offset="0" stop-color="#22c55e" stop-opacity=".55"/><stop offset="1" stop-color="#22c55e" stop-opacity="0"/></radialGradient>
          <filter id="soft"><feGaussianBlur stdDeviation="6"/></filter>
        </defs>

        <!-- Scene 1: the merchant applies -->
        <g class="merchant" [class.show]="scene() === 'cup'">
          <g class="cup" transform="translate(380 300)">
            <path d="M-70 -60 h140 l-14 200 q0 20 -20 20 h-72 q-20 0 -20 -20 z" fill="#d7b58f"/>
            <path d="M-78 -60 h156 v-26 q0 -10 -10 -10 h-136 q-10 0 -10 10 z" fill="#f8fafc"/>
            <path d="M-40 -96 h80 v-14 q0 -6 -6 -6 h-68 q-6 0 -6 6 z" fill="#e2e8f0"/>
            <path d="M-76 20 h152 l-6 80 h-140 z" fill="#0f766e"/>
            <path d="M0 38 q22 6 20 34 q-22 -4 -20 -34 z M0 38 q-4 16 -10 30" fill="#5eead4" stroke="#5eead4" stroke-width="2" stroke-linecap="round"/>
            <text x="0" y="90" text-anchor="middle" font-size="11" fill="#ccfbf1" font-weight="700" letter-spacing="1.5">NITIN COFFEE CO</text>
            <path class="steam" d="M-30 -125 q10 -20 0 -40 q-10 -20 0 -40" fill="none" stroke="#e2e8f0" stroke-width="4" stroke-linecap="round"/>
            <path class="steam s2" d="M0 -125 q10 -20 0 -40 q-10 -20 0 -40" fill="none" stroke="#e2e8f0" stroke-width="4" stroke-linecap="round"/>
            <path class="steam s3" d="M30 -125 q10 -20 0 -40 q-10 -20 0 -40" fill="none" stroke="#e2e8f0" stroke-width="4" stroke-linecap="round"/>
          </g>
          <defs>
            <marker id="arrow-head" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
              <path d="M0 0 L10 5 L0 10 z" fill="#22c55e"/>
            </marker>
          </defs>
          <path class="hand a1" d="M470 300 L540 300" marker-end="url(#arrow-head)"/>
          <path class="hand a2" d="M660 305 L738 315" marker-end="url(#arrow-head)"/>
          <g class="application">
            <rect x="-46" y="-60" width="92" height="120" rx="8" fill="#fff" stroke="#cbd5e1"/>
            <rect x="-30" y="-40" width="60" height="6" rx="3" fill="#94a3b8"/>
            <rect x="-30" y="-24" width="46" height="6" rx="3" fill="#cbd5e1"/>
            <rect x="-30" y="-8" width="56" height="6" rx="3" fill="#cbd5e1"/>
            <rect x="-30" y="8" width="38" height="6" rx="3" fill="#cbd5e1"/>
            <rect x="-30" y="30" width="60" height="16" rx="4" fill="#16a34a"/>
            <text x="0" y="42" text-anchor="middle" font-size="10" fill="#fff" font-weight="700">APPLY</text>
          </g>
          <g class="pos" transform="translate(820 320)">
            <rect x="-70" y="-110" width="140" height="220" rx="18" fill="#1e293b" stroke="#334155" stroke-width="3"/>
            <rect x="-54" y="-92" width="108" height="76" rx="8" fill="#0ea5e9" opacity=".9"/>
            <text x="0" y="-52" text-anchor="middle" font-size="13" fill="#fff" font-weight="600">POS · $40.00</text>
            @for (r of [0, 1, 2]; track r) {
              @for (c of [0, 1, 2]; track c) {
                <rect [attr.x]="-50 + c * 36" [attr.y]="0 + r * 30" width="28" height="22" rx="5" fill="#334155"/>
              }
            }
            <rect x="-30" y="-120" width="60" height="8" rx="4" fill="#475569"/>
          </g>
          <text class="lead" x="600" y="600" text-anchor="middle">Nitin Coffee Co · Austin, TX applies for a card-present POS terminal</text>
        </g>

        <!-- Agent frames -->
        @for (a of agents; track a.id) {
          <g class="agent" [class.show]="showAgents()" [attr.transform]="'translate(' + a.x + ' ' + a.y + ')'">
            <rect x="0" y="0" [attr.width]="a.w" [attr.height]="a.h" rx="20" fill="url(#agentFill)" stroke="#334155" stroke-width="1.5"/>
            @if (a.layout === 'row') {
              <text x="18" y="26" class="agent-name">{{ a.name }}</text>
              <text x="18" y="44" class="agent-mandate">{{ a.mandate }}</text>
            } @else {
              <text [attr.x]="a.w / 2" y="36" text-anchor="middle" class="agent-name">{{ a.name }}</text>
              <text [attr.x]="a.w / 2" y="56" text-anchor="middle" class="agent-mandate">{{ a.mandate }}</text>
            }
          </g>
        }
        <text class="lane-note" [class.show]="showAgents()" x="15" y="578" text-anchor="middle">Profile first</text>
        <text class="lane-note" [class.show]="showAgents()" x="560" y="578" text-anchor="middle">…then three agents run in parallel</text>
        <text class="lane-note" [class.show]="showAgents()" x="1170" y="578" text-anchor="middle">…then Decision</text>
        <!-- Stage connectors: Profile feeds each parallel lane, each lane feeds the Decision agent -->
        @for (i of lanes; track i) {
          <path class="stage-link" [class.show]="scene() === 'pipeline' || scene() === 'score'" [class.flow]="scene() === 'pipeline'"
                [attr.d]="laneLink(agents[profileIndex], agents[i])"/>
          <path class="stage-link" [class.show]="scene() === 'pipeline' || scene() === 'score'" [class.flow]="scene() === 'pipeline'"
                [attr.d]="laneLink(agents[i], agents[decisionIndex])"/>
        }

        <!-- Pipeline edges -->
        @for (e of edges; track e.id) {
          <path class="edge" [class.show]="scene() === 'pipeline' || scene() === 'score'" [class.flow]="scene() === 'pipeline'"
                [attr.d]="edgePath(e.from, e.to)" [style.animation-delay]="e.delay + 'ms'"/>
        }

        <!-- Steps -->
        @for (s of steps; track s.id) {
          <g class="step" [class.show]="showSteps()" [class.lit]="litSteps().has(s.id)"
             [attr.transform]="'translate(' + stepPos(s.id).x + ' ' + stepPos(s.id).y + ')'">
            <rect x="-100" y="-20" width="200" height="40" rx="10"/>
            <circle class="dot" cx="-82" cy="0" r="6"/>
            <text x="-68" y="4" class="step-label">{{ s.label }}</text>
            <circle class="halo" cx="0" cy="0" r="60" fill="url(#glow)"/>
          </g>
        }

        <!-- Signals -->
        @for (sg of signals; track sg.id) {
          <g class="signal" [class.show]="scene() === 'signals' || scene() === 'steps'" [class.merging]="scene() === 'steps'"
             [attr.transform]="'translate(' + signalPos(sg).x + ' ' + signalPos(sg).y + ')'" [style.transition-delay]="sg.delay + 'ms'">
            <rect [attr.x]="-sg.w / 2" y="-14" [attr.width]="sg.w" height="28" rx="14"/>
            <circle [attr.cx]="-sg.w / 2 + 14" cy="0" r="4"/>
            <text [attr.x]="-sg.w / 2 + 26" y="4">{{ sg.label }}</text>
          </g>
        }

        <!-- Score -->
        <g class="verdict" [class.show]="scene() === 'score'" transform="translate(600 330)">
          <circle r="150" fill="#0f172a" stroke="#1e293b" stroke-width="2"/>
          <circle class="track" r="128" fill="none" stroke="#1e293b" stroke-width="16"/>
          <circle class="arc" r="128" fill="none" [attr.stroke]="tierColor()" stroke-width="16" stroke-linecap="round"
                  [attr.stroke-dasharray]="arcLength" [attr.stroke-dashoffset]="arcLength * (1 - shownScore() / 1000)" transform="rotate(-90)"/>
          <text class="score-num" y="12" text-anchor="middle">{{ shownScore() }}</text>
          <text class="score-cap" y="46" text-anchor="middle">/ 1000 · {{ verdict()?.tier ?? '—' }} risk</text>
          <text class="score-out" y="82" text-anchor="middle" [attr.fill]="tierColor()">{{ verdict()?.outcome ?? '' }}</text>
        </g>
      </svg>

      @if (scene() === 'memo') {
        <div class="memo-view" #memoView>
          <article class="memo" #memoPage [style.transform]="memoTransform()">
            <header class="m-head" id="m-top">
              <div>
                <div class="m-title">Merchant Intelligence · Underwriting Assessment</div>
                <div class="m-sub">Nitin Coffee Co LLC (t/a Nitin Coffee Co)</div>
              </div>
              <div class="m-badge"><b>APPROVE</b><span>Score 889/1000 · VeryLow</span></div>
            </header>
            <p class="m-headline">APPROVE — Nitin Coffee Co LLC: score 889/1000 (VeryLow), 12 of 12 checks covered (100% score-signal coverage), 0 high / 0 medium finding(s).</p>
            <p>Long-established card-present coffeehouse with a verified registry record, confirmed storefront and clean screening. Financials and card deposits support the declared volume; the credit model approves with high confidence.</p>
            <h4>Assessment</h4>
            <dl>
              <dt>Reference</dt><dd>ASMT-DEMO-NCC-0001</dd>
              <dt>Analyst</dt><dd>analyst</dd>
              <dt>Rule set</dt><dd>v3 · deciding rule AUTO_APPROVE</dd>
              <dt>Coverage</dt><dd>12 of 12 checks covered · 100% score-signal coverage</dd>
            </dl>
            <h4>Merchant intake</h4>
            <dl>
              <dt>Legal name</dt><dd>Nitin Coffee Co LLC</dd>
              <dt>Address</dt><dd>600 Congress Ave, Austin, TX 78701, US</dd>
              <dt>Website</dt><dd>https://nitincoffee.co</dd>
              <dt>Description</dt><dd>Independent coffeehouse serving brewed coffee, espresso drinks and pastries in-store.</dd>
              <dt>MCC</dt><dd>5814</dd>
              <dt>Declared volume</dt><dd>$900,000 / year · avg ticket $40 · max $400</dd>
              <dt>Delivery / CNP</dt><dd>0 days · 30% card-not-present</dd>
              <dt>Owners</dt><dd>Nitin Rawat 100% (Owner)</dd>
            </dl>
            <h4>Check outcomes</h4>
            <table>
              <thead><tr><th>Check</th><th>Result</th><th>Detail</th><th>Severity</th></tr></thead>
              <tbody>
                @for (c of memoChecks; track c.check) {
                  <tr [attr.id]="c.id ?? null" [class.focus]="memoFocus() === c.id">
                    <td><b>{{ c.check }}</b></td><td>{{ c.result }}</td><td class="small">{{ c.detail }}</td><td class="sev">{{ c.sev }}</td>
                  </tr>
                }
              </tbody>
            </table>
            <h4 id="m-score">Unified risk score</h4>
            <table [class.focus]="memoFocus() === 'm-score'">
              <thead><tr><th>Component</th><th>Weight</th><th>Score</th><th>Weighted</th><th>Detail</th></tr></thead>
              <tbody>
                @for (r of memoScore; track r[0]) {
                  <tr><td>{{ r[0] }}</td><td>{{ r[1] }}</td><td>{{ r[2] }}</td><td>{{ r[3] }}</td><td class="small">{{ r[4] }}</td></tr>
                }
                <tr class="total"><td>Unified score</td><td></td><td></td><td>88.9 → 889</td><td class="small">Tier VeryLow · no hard stops · no coverage gaps</td></tr>
              </tbody>
            </table>
            <h4 id="m-credit">Credit model explainability (Shapley contributions)</h4>
            <table [class.focus]="memoFocus() === 'm-credit'">
              <thead><tr><th>Feature</th><th>Value</th><th>Baseline</th><th>Contribution</th><th>Direction</th></tr></thead>
              <tbody>
                @for (r of memoShap; track r[0]) {
                  <tr><td>{{ r[0] }}</td><td>{{ r[1] }}</td><td class="muted">{{ r[2] }}</td><td [class.pos]="r[3].startsWith('+')" [class.neg]="r[3].startsWith('-')">{{ r[3] }}</td><td>{{ r[4] }}</td></tr>
                }
              </tbody>
            </table>
            <p class="small italic">Decision Approved with 96.3% confidence. P(Approved) moved from 93.1% for a typical merchant to 96.3%; main drivers: ExistingRelationship=True (+3.9 pts), YearsInBusiness=15 (+1.6 pts).</p>
            <h4 id="m-rule">Policy rules</h4>
            <div class="rule" [class.focus]="memoFocus() === 'm-rule'">
              <code>AUTO_APPROVE</code><span class="out">Approve</span><span class="small">Strong score, adequate coverage, no high-severity findings (priority 100)</span>
            </div>
            <h4>Recommended terms</h4>
            <dl>
              <dt>Risk band</dt><dd>A</dd>
              <dt>Reserve</dt><dd>None</dd>
              <dt>Pricing</dt><dd>Interchange + 20 bps + $0.10 per transaction</dd>
              <dt>Settlement</dt><dd>T+1</dd>
              <dt>Monitoring</dt><dd>Standard velocity and chargeback monitoring</dd>
            </dl>
            <footer class="m-foot" id="m-bottom">Illustrative memo for a fictional merchant · Merchant Intelligence by Nitin Rawat · page 1 of 1</footer>
          </article>
        </div>
      }

      @if (scene() === 'score') {
        <footer class="verdict-foot">
          <div class="chips">
            <span class="chip">Coverage {{ verdict()?.coveragePercent ?? '—' }}%</span>
            <span class="chip">16 checks · 5 agents · 1 decision</span>
            <span class="chip ok">Illustrative result for a low-risk coffeehouse</span>
          </div>
          <button mat-flat-button color="primary" class="enter" (click)="finish()">Enter the workbench<mat-icon>arrow_forward</mat-icon></button>
        </footer>
      }
    </div>
  `,
  styles: [`
    :host { position: fixed; inset: 0; z-index: 1000; display: block; }
    .film { position: absolute; inset: 0; background: radial-gradient(1200px 700px at 50% 40%, #0b1a3a 0%, #060b18 60%, #030610 100%); color: #e2e8f0; overflow: hidden; font-family: inherit; }
    .stars { position: absolute; inset: 0; background-image: radial-gradient(1px 1px at 20% 30%, rgba(255,255,255,.35) 50%, transparent 51%), radial-gradient(1px 1px at 70% 20%, rgba(255,255,255,.3) 50%, transparent 51%), radial-gradient(1px 1px at 40% 80%, rgba(255,255,255,.25) 50%, transparent 51%), radial-gradient(1px 1px at 85% 65%, rgba(255,255,255,.3) 50%, transparent 51%), radial-gradient(1px 1px at 10% 70%, rgba(255,255,255,.25) 50%, transparent 51%); opacity: .8; }

    .hud { position: absolute; top: 0; left: 0; right: 0; display: flex; align-items: center; gap: 16px; padding: 18px 28px; z-index: 2; }
    .brand { display: inline-flex; align-items: center; gap: 8px; font-weight: 600; color: #cbd5e1; }
    .brand mat-icon { color: #8fb0ff; }
    .caption { flex: 1; text-align: center; font-size: 15px; color: #94a3b8; letter-spacing: .01em; transition: opacity .4s; }
    .skip { color: #cbd5e1 !important; border-color: rgba(255,255,255,.2) !important; }
    .skip mat-icon { margin-right: 4px; }

    .stage { position: absolute; inset: 0; width: 100%; height: 100%; }

    /* generic reveal */
    .merchant, .agent, .step, .signal, .verdict, .edge, .stage-link { transition: opacity .8s ease, transform 1.4s cubic-bezier(.2,.8,.2,1); }
    .merchant, .agent, .step, .signal, .verdict { opacity: 0; }
    .show { opacity: 1; }

    /* scene 1 */
    .steam { opacity: .7; animation: steam 2.4s ease-in-out infinite; }
    .steam.s2 { animation-delay: .5s; } .steam.s3 { animation-delay: 1s; }
    @keyframes steam { 0%,100% { transform: translateY(0); opacity: .2; } 50% { transform: translateY(-14px); opacity: .8; } }
    .application { transform: translate(380px, 300px) scale(.4); opacity: 0; transition: transform 1.6s cubic-bezier(.2,.8,.2,1) .8s, opacity .6s .8s; }
    .merchant.show .application { transform: translate(600px, 300px) scale(1); opacity: 1; }
    .film[data-scene='cup'] .pos { animation: pos-pop .6s ease 2.2s both; }
    @keyframes pos-pop { from { transform: translate(820px, 320px) scale(.9); } to { transform: translate(820px, 320px) scale(1); } }
    .lead { font-size: 22px; fill: #cbd5e1; font-weight: 500; }
    .hand { fill: none; stroke: #22c55e; stroke-width: 3; stroke-linecap: round; stroke-dasharray: 10 10; opacity: 0; filter: drop-shadow(0 0 6px rgba(34,197,94,.6)); }
    .film[data-scene='cup'] .hand { animation: flow .6s linear infinite, hand-in .5s ease both; }
    .film[data-scene='cup'] .hand.a1 { animation-delay: 1.6s, 1.6s; }
    .film[data-scene='cup'] .hand.a2 { animation-delay: 2.7s, 2.7s; }
    @keyframes hand-in { from { opacity: 0; transform: translateX(-10px); } to { opacity: 1; transform: none; } }

    /* agents */
    .agent-name { fill: #fff; font-size: 15px; font-weight: 600; }
    .agent-mandate { fill: #94a3b8; font-size: 11px; }
    .lane-note { fill: #64748b; font-size: 12px; font-style: italic; opacity: 0; transition: opacity .8s ease; }
    .lane-note.show { opacity: 1; }

    /* steps */
    .step rect { fill: #111c33; stroke: #334155; stroke-width: 1.5; transition: fill .5s, stroke .5s; }
    .step .dot { fill: #475569; transition: fill .5s; }
    .step .halo { opacity: 0; transition: opacity .6s; }
    .step-label { fill: #e2e8f0; font-size: 12.5px; font-weight: 500; }
    .step.lit rect { fill: #0f2a1f; stroke: #22c55e; }
    .step.lit .dot { fill: #22c55e; }
    .step.lit .halo { opacity: 1; }

    /* signals */
    .signal rect { fill: rgba(79,124,255,.18); stroke: #4f7cff; stroke-width: 1.2; }
    .signal circle { fill: #8fb0ff; }
    .signal text { fill: #dbe4ff; font-size: 11.5px; font-weight: 500; }
    .signal.show { animation: drift 4s ease-in-out infinite alternate; }
    .signal.merging { opacity: 0; transition: opacity .6s ease 1.1s, transform 1.4s cubic-bezier(.2,.8,.2,1); animation: none; }
    @keyframes drift { from { translate: 0 -4px; } to { translate: 0 4px; } }

    /* pipeline */
    .edge, .stage-link { fill: none; stroke: #22c55e; stroke-width: 2.5; opacity: 0; }
    .stage-link { stroke-width: 4; stroke-dasharray: 10 14; }
    .edge.show, .stage-link.show { opacity: .85; }
    .edge.flow { stroke-dasharray: 8 12; animation: flow 1s linear infinite; }
    .stage-link.flow { animation: flow .7s linear infinite; }
    @keyframes flow { to { stroke-dashoffset: -40; } }
    .film[data-scene='score'] .edge, .film[data-scene='score'] .stage-link, .film[data-scene='score'] .agent, .film[data-scene='score'] .step { opacity: .18; }
    .film[data-scene='memo'] .stage { opacity: .12; transition: opacity .8s; }

    /* memo */
    .memo-view { position: absolute; left: 50%; top: 64px; bottom: 24px; width: min(720px, 92vw); margin-left: calc(min(720px, 92vw) / -2); overflow: hidden; border-radius: 6px; box-shadow: 0 30px 80px rgba(0,0,0,.6); animation: fade .8s ease both; }
    .memo { position: absolute; left: 0; top: 0; width: 100%; box-sizing: border-box; padding: 34px 40px 40px; background: #fff; color: #1e293b; font-size: 10.5px; line-height: 1.45; transform-origin: 0 0; transition: transform 1.5s cubic-bezier(.65,0,.35,1); }
    .m-head { display: flex; justify-content: space-between; align-items: flex-start; padding-bottom: 8px; border-bottom: 1px solid #d0d7de; }
    .m-title { font-size: 17px; font-weight: 600; }
    .m-sub { font-size: 13px; color: #64748b; margin-top: 2px; }
    .m-badge { width: 150px; text-align: center; }
    .m-badge b { display: block; background: #16a34a; color: #fff; font-size: 14px; padding: 6px; }
    .m-badge span { display: block; font-size: 9px; color: #64748b; margin-top: 3px; }
    .m-headline { font-weight: 600; font-size: 11.5px; margin: 10px 0 4px; }
    .memo p { margin: 0 0 8px; }
    .memo h4 { margin: 14px 0 4px; font-size: 11px; font-weight: 600; color: #0f172a; border-bottom: 1px solid #d0d7de; padding-bottom: 3px; }
    .memo dl { display: grid; grid-template-columns: 130px 1fr; gap: 2px 10px; margin: 0; }
    .memo dt { color: #64748b; } .memo dd { margin: 0; }
    .memo table { width: 100%; border-collapse: collapse; }
    .memo th { background: #f0f4f8; text-align: left; padding: 4px; font-size: 9.5px; font-weight: 600; }
    .memo td { padding: 4px; border-bottom: .5px solid #d0d7de; vertical-align: top; }
    .memo .small { font-size: 9.5px; } .memo .muted { color: #64748b; } .memo .italic { font-style: italic; }
    .memo .sev { color: #16a34a; font-weight: 600; }
    .memo .pos { color: #16a34a; } .memo .neg { color: #dc2626; }
    .memo .total td { font-weight: 600; border-top: 1.5px solid #64748b; }
    .memo .rule { display: flex; gap: 12px; align-items: baseline; padding: 4px; }
    .memo code { font-family: 'Courier New', monospace; font-size: 9.5px; font-weight: 600; }
    .memo .out { color: #16a34a; font-weight: 600; }
    .memo .focus { outline: 2px solid #22c55e; outline-offset: 2px; background: rgba(34,197,94,.08); transition: outline-color .4s, background .4s; }
    .m-foot { margin-top: 22px; padding-top: 6px; border-top: 1px solid #d0d7de; font-size: 9px; color: #64748b; text-align: center; }

    /* score */
    .verdict { transform: translate(600px, 330px) scale(.8); }
    .verdict.show { transform: translate(600px, 330px) scale(1); }
    .arc { transition: stroke-dashoffset .3s linear; filter: drop-shadow(0 0 12px rgba(34,197,94,.5)); }
    .score-num { fill: #fff; font-size: 84px; font-weight: 700; letter-spacing: -.03em; }
    .score-cap { fill: #94a3b8; font-size: 15px; }
    .score-out { font-size: 22px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; }

    .verdict-foot { position: absolute; left: 0; right: 0; bottom: 36px; display: flex; flex-direction: column; align-items: center; gap: 18px; animation: fade .8s ease .6s both; }
    @keyframes fade { from { opacity: 0; transform: translateY(8px); } to { opacity: 1; transform: none; } }
    .chips { display: flex; gap: 10px; flex-wrap: wrap; justify-content: center; }
    .chip { padding: 6px 12px; border-radius: 999px; border: 1px solid rgba(255,255,255,.15); font-size: 12px; color: #cbd5e1; background: rgba(255,255,255,.04); }
    .chip.ok { border-color: rgba(34,197,94,.5); color: #86efac; }
    .enter { padding: 0 22px !important; height: 44px !important; font-size: 15px !important; }
    .enter mat-icon { margin-left: 6px; }

    @media (prefers-reduced-motion: reduce) { .merchant, .agent, .step, .signal, .verdict, .edge, .stage-link, .application { transition: none; animation: none !important; } }
  `]
})
export class IntroComponent {
  private readonly destroyRef = inject(DestroyRef);
  readonly done = output<void>();

  readonly agents = AGENTS;
  readonly lanes = LANES;
  readonly profileIndex = PROFILE;
  readonly decisionIndex = DECISION;
  readonly steps = STEPS;
  readonly signals = SIGNALS.map((s, i) => ({ ...s, w: Math.max(120, s.label.length * 7 + 44), delay: (i * 90) % 700 }));
  readonly edges = EDGES.map(([from, to], i) => ({ id: `${from}-${to}`, from, to, delay: i * 60 }));
  readonly arcLength = 2 * Math.PI * 128;

  readonly scene = signal<Scene>('cup');
  readonly litSteps = signal<Set<string>>(new Set());
  readonly shownScore = signal(0);
  readonly verdict = signal<{ score: number; tier: string; outcome: string; coveragePercent: number } | null>(null);

  readonly memoChecks = MEMO_CHECKS;
  readonly memoScore = MEMO_SCORE;
  readonly memoShap = MEMO_SHAP;
  readonly memoFocus = signal<string | null>(null);
  readonly memoTransform = signal('translateY(0) scale(1)');
  private readonly memoView = viewChild<ElementRef<HTMLElement>>('memoView');
  private readonly memoPage = viewChild<ElementRef<HTMLElement>>('memoPage');

  readonly showSteps = computed(() => ['steps', 'agents', 'pipeline', 'memo', 'score'].includes(this.scene()));
  readonly showAgents = computed(() => ['agents', 'pipeline', 'memo', 'score'].includes(this.scene()));
  readonly caption = computed(() => ({
    cup: 'A merchant applies. One application, one question: can we board them safely?',
    signals: 'Every field on the application is a signal — identity, web, volume, ownership, documents.',
    steps: 'Signals merge into 16 assessment checks…',
    agents: '…which group into five agents: Profile classifies the applicant first, then Pre-check, KYB and Financial run in parallel, then Decision.',
    pipeline: 'The workflow wires the checks into a pipeline and evidence flows through it.',
    memo: 'Every finding lands in an audit-ready underwriting memo — the analyst reads evidence, not opinions.',
    score: 'One unified 0–1000 risk score, a policy outcome, and an audit-ready case.'
  } as Record<Scene, string>)[this.scene()]);
  readonly tierColor = computed(() => {
    const t = this.verdict()?.tier ?? '';
    if (t === 'VeryLow' || t === 'Low') return '#22c55e';
    if (t === 'Medium') return '#f59e0b';
    return t ? '#ef4444' : '#22c55e';
  });

  private readonly timers: ReturnType<typeof setTimeout>[] = [];
  private readonly stepGrid = new Map<string, Pt>();
  private readonly stepAgent = new Map<string, Pt>();
  private readonly signalScatter = new Map<string, Pt>();

  constructor() {
    STEP_ORDER.forEach((id, i) => {
      const row = Math.floor(i / 4), col = i % 4;
      this.stepGrid.set(id, { x: 270 + col * 220, y: 215 + row * 88 });
    });
    for (const a of AGENTS) a.steps.forEach((id, i) => this.stepAgent.set(id, a.layout === 'row'
      ? { x: a.x + 115 + i * 210, y: a.y + 80 }
      : { x: a.x + a.w / 2, y: a.y + 100 + i * 100 }));
    this.signals.forEach((s, i) => {
      const angle = (i / this.signals.length) * Math.PI * 2 - Math.PI / 2;
      const r = i % 2 === 0 ? 170 : 275;
      this.signalScatter.set(s.id, { x: 600 + Math.cos(angle) * r * 1.7, y: 350 + Math.sin(angle) * r });
    });

    this.schedule();
    this.destroyRef.onDestroy(() => this.timers.forEach(clearTimeout));
  }

  private schedule(): void {
    let at = 0;
    for (let i = 1; i < SCENES.length; i++) {
      at += SCENES[i - 1].ms;
      const name = SCENES[i].name;
      this.timers.push(setTimeout(() => this.enter(name), at));
    }
  }

  private runMemoCamera(): void {
    let at = 50;
    for (const shot of MEMO_SHOTS) {
      this.timers.push(setTimeout(() => this.aimMemo(shot.target, shot.zoom), at));
      at += shot.ms;
    }
  }

  private aimMemo(target: string, zoom: number): void {
    const view = this.memoView()?.nativeElement, page = this.memoPage()?.nativeElement;
    const el = page?.querySelector<HTMLElement>(`#${target}`);
    if (!view || !page || !el) return;
    const viewH = view.clientHeight;
    const pageH = page.offsetHeight;
    let y: number;
    if (target === 'm-top') y = 0;
    else if (target === 'm-bottom') y = Math.max(0, pageH - viewH);
    else y = Math.max(0, el.offsetTop + el.offsetHeight / 2 - viewH / (2 * zoom));
    y = Math.min(y, Math.max(0, pageH - viewH / zoom));
    this.memoTransform.set(`translateY(${-y * zoom}px) scale(${zoom})`);
    this.memoFocus.set(zoom > 1 ? target : null);
  }

  private enter(scene: Scene): void {
    this.scene.set(scene);
    if (scene === 'pipeline') {
      STEP_ORDER.forEach((id, i) => this.timers.push(setTimeout(() => this.litSteps.update(s => new Set([...s, id])), 600 + i * 300)));
    }
    if (scene === 'memo') {
      this.runMemoCamera();
    }
    if (scene === 'score') {
      this.verdict.set(VERDICT);
      const target = this.verdict()!.score;
      const start = performance.now();
      const tick = (now: number) => {
        const p = Math.min(1, (now - start) / 2200);
        const eased = 1 - Math.pow(1 - p, 3);
        this.shownScore.set(Math.round(target * eased));
        if (p < 1) requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    }
  }

  stepPos(id: string): Pt {
    const sc = this.scene();
    if (sc === 'cup' || sc === 'signals') return { x: 600, y: 340 };
    if (sc === 'steps') return this.stepGrid.get(id)!;
    return this.stepAgent.get(id)!;
  }

  signalPos(sg: SignalDef): Pt {
    const sc = this.scene();
    if (sc === 'cup') return { x: 600, y: 300 };
    if (sc === 'signals') return this.signalScatter.get(sg.id)!;
    return this.stepGrid.get(sg.step)!;
  }

  laneLink(from: AgentDef, to: AgentDef): string {
    const x1 = from.x + from.w, y1 = from.y + from.h / 2;
    const x2 = to.x, y2 = to.y + to.h / 2;
    const mx = (x1 + x2) / 2;
    return `M${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2} ${y2}`;
  }

  edgePath(from: string, to: string): string {
    const a = this.stepAgent.get(from)!, b = this.stepAgent.get(to)!;
    if (a.y === b.y) return `M${a.x + 100} ${a.y} L ${b.x - 100} ${b.y}`;
    if (a.x === b.x) return `M${a.x} ${a.y + 20} L ${b.x} ${b.y - 20}`;
    const mx = (a.x + 100 + b.x - 100) / 2;
    return `M${a.x + 100} ${a.y} C ${mx} ${a.y}, ${mx} ${b.y}, ${b.x - 100} ${b.y}`;
  }

  finish(): void {
    this.timers.forEach(clearTimeout);
    this.done.emit();
  }
}
