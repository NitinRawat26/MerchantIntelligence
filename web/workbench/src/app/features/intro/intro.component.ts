import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { SuiteApiService } from '../../shared/suite-api.service';

/** Scenes of the intro film; each scene owns a set of positions the SVG elements transition to. */
type Scene = 'cup' | 'signals' | 'steps' | 'agents' | 'pipeline' | 'score';
const SCENES: { name: Scene; ms: number }[] = [
  { name: 'cup', ms: 3600 },
  { name: 'signals', ms: 3800 },
  { name: 'steps', ms: 3600 },
  { name: 'agents', ms: 3200 },
  { name: 'pipeline', ms: 5600 },
  { name: 'score', ms: 0 }
];

interface Pt { x: number; y: number; }
interface AgentDef { id: string; name: string; mandate: string; steps: string[]; x: number; }
interface StepDef { id: string; label: string; agent: string; }
interface SignalDef { id: string; label: string; step: string; }

const AGENTS: AgentDef[] = [
  { id: 'precheck', name: 'Pre-check agent', mandate: 'Website · prohibited · MCC', steps: ['website', 'prohibited', 'mcc'], x: 170 },
  { id: 'kyb', name: 'KYB agent', mandate: 'Identity · screening · MATCH · presence', steps: ['verification', 'screening', 'match', 'presence'], x: 460 },
  { id: 'financial', name: 'Financial agent', mandate: 'Bank · P&L · plausibility · credit', steps: ['bank', 'financials', 'plausibility', 'credit'], x: 750 },
  { id: 'decision', name: 'Decision agent', mandate: 'Terms · score · case', steps: ['terms', 'score', 'case'], x: 1040 }
];

const STEPS: StepDef[] = [
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
  { id: 'url', label: 'starbucks.com', step: 'website' },
  { id: 'desc', label: 'Coffeehouse chain', step: 'prohibited' },
  { id: 'mcc', label: 'MCC 5814', step: 'mcc' },
  { id: 'legal', label: 'Starbucks Corporation', step: 'verification' },
  { id: 'owner', label: 'Owner · Brian Niccol', step: 'screening' },
  { id: 'rel', label: 'Existing relationship', step: 'match' },
  { id: 'addr', label: 'Seattle, WA 98134', step: 'presence' },
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
  ['website', 'prohibited'], ['website', 'mcc'],
  ['mcc', 'verification'], ['prohibited', 'verification'],
  ['verification', 'screening'], ['verification', 'match'], ['verification', 'presence'],
  ['screening', 'bank'], ['match', 'financials'], ['presence', 'bank'],
  ['bank', 'plausibility'], ['financials', 'plausibility'], ['plausibility', 'credit'],
  ['credit', 'terms'], ['terms', 'score'], ['score', 'case']
];

const STEP_ORDER = ['website', 'prohibited', 'mcc', 'verification', 'screening', 'match', 'presence', 'bank', 'financials', 'plausibility', 'credit', 'terms', 'score', 'case'];

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

      <svg class="stage" viewBox="0 0 1200 700" preserveAspectRatio="xMidYMid meet" aria-hidden="true">
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
            <path d="M-70 -60 h140 l-14 200 q0 20 -20 20 h-72 q-20 0 -20 -20 z" fill="#f8fafc"/>
            <path d="M-78 -60 h156 v-26 q0 -10 -10 -10 h-136 q-10 0 -10 10 z" fill="#0f5132"/>
            <path d="M-40 -96 h80 v-14 q0 -6 -6 -6 h-68 q-6 0 -6 6 z" fill="#0b3d25"/>
            <rect x="-84" y="40" width="168" height="44" fill="#0f5132"/>
            <circle cx="0" cy="62" r="34" fill="#00704a" stroke="#fff" stroke-width="3"/>
            <path d="M0 -20 l6 12 14 2 -10 10 2 14 -12 -6 -12 6 2 -14 -10 -10 14 -2 z" transform="translate(0 62) scale(1.2)" fill="#fff"/>
            <path class="steam" d="M-30 -125 q10 -20 0 -40 q-10 -20 0 -40" fill="none" stroke="#e2e8f0" stroke-width="4" stroke-linecap="round"/>
            <path class="steam s2" d="M0 -125 q10 -20 0 -40 q-10 -20 0 -40" fill="none" stroke="#e2e8f0" stroke-width="4" stroke-linecap="round"/>
            <path class="steam s3" d="M30 -125 q10 -20 0 -40 q-10 -20 0 -40" fill="none" stroke="#e2e8f0" stroke-width="4" stroke-linecap="round"/>
          </g>
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
          <text class="lead" x="600" y="600" text-anchor="middle">Starbucks · Seattle, WA applies for a card-present POS terminal</text>
        </g>

        <!-- Agent frames -->
        @for (a of agents; track a.id) {
          <g class="agent" [class.show]="showAgents()" [attr.transform]="'translate(' + a.x + ' 0)'">
            <rect x="-125" y="150" width="250" height="400" rx="20" fill="url(#agentFill)" stroke="#334155" stroke-width="1.5"/>
            <text x="0" y="185" text-anchor="middle" class="agent-name">{{ a.name }}</text>
            <text x="0" y="205" text-anchor="middle" class="agent-mandate">{{ a.mandate }}</text>
          </g>
        }
        <!-- Stage connectors -->
        @for (i of [0, 1, 2]; track i) {
          <path class="stage-link" [class.show]="scene() === 'pipeline' || scene() === 'score'" [class.flow]="scene() === 'pipeline'"
                [attr.d]="'M' + (agents[i].x + 125) + ' 350 L ' + (agents[i + 1].x - 125) + ' 350'"/>
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

      @if (scene() === 'score') {
        <footer class="verdict-foot">
          <div class="chips">
            <span class="chip">Coverage {{ verdict()?.coveragePercent ?? '—' }}%</span>
            <span class="chip">14 checks · 4 agents · 1 decision</span>
            @if (verdict()?.source === 'live') { <span class="chip ok">From the latest Starbucks assessment</span> }
            @else { <span class="chip">Illustrative score · run the preset to refresh</span> }
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

    /* agents */
    .agent-name { fill: #fff; font-size: 15px; font-weight: 600; }
    .agent-mandate { fill: #94a3b8; font-size: 11px; }

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
  private readonly api = inject(SuiteApiService);
  private readonly destroyRef = inject(DestroyRef);
  readonly done = output<void>();

  readonly agents = AGENTS;
  readonly steps = STEPS;
  readonly signals = SIGNALS.map((s, i) => ({ ...s, w: Math.max(120, s.label.length * 7 + 44), delay: (i * 90) % 700 }));
  readonly edges = EDGES.map(([from, to], i) => ({ id: `${from}-${to}`, from, to, delay: i * 60 }));
  readonly arcLength = 2 * Math.PI * 128;

  readonly scene = signal<Scene>('cup');
  readonly litSteps = signal<Set<string>>(new Set());
  readonly shownScore = signal(0);
  readonly verdict = signal<{ score: number; tier: string; outcome: string; coveragePercent: number; source: 'live' | 'fallback' } | null>(null);

  readonly showSteps = computed(() => ['steps', 'agents', 'pipeline', 'score'].includes(this.scene()));
  readonly showAgents = computed(() => ['agents', 'pipeline', 'score'].includes(this.scene()));
  readonly caption = computed(() => ({
    cup: 'A merchant applies. One application, one question: can we board them safely?',
    signals: 'Every field on the application is a signal — identity, web, volume, ownership, documents.',
    steps: 'Signals merge into 14 assessment checks…',
    agents: '…which group into four specialised agents.',
    pipeline: 'The workflow wires the checks into a pipeline and evidence flows through it.',
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
      const row = Math.floor(i / 5), col = i % 5;
      const offset = row === 2 ? 110 : 0;
      this.stepGrid.set(id, { x: 160 + col * 220 + offset, y: 250 + row * 90 });
    });
    for (const a of AGENTS) a.steps.forEach((id, i) => this.stepAgent.set(id, { x: a.x, y: 250 + i * 66 }));
    this.signals.forEach((s, i) => {
      const angle = (i / this.signals.length) * Math.PI * 2 - Math.PI / 2;
      const r = i % 2 === 0 ? 170 : 275;
      this.signalScatter.set(s.id, { x: 600 + Math.cos(angle) * r * 1.7, y: 350 + Math.sin(angle) * r });
    });

    this.api.assessments(50).pipe(takeUntilDestroyed()).subscribe({
      next: list => {
        const hit = list.find(a => /starbucks/i.test(a.merchantName));
        if (hit) this.verdict.set({ score: hit.score, tier: hit.tier, outcome: hit.outcome, coveragePercent: Math.round(hit.coveragePercent), source: 'live' });
      },
      error: () => { /* fall back to the illustrative verdict */ }
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

  private enter(scene: Scene): void {
    this.scene.set(scene);
    if (scene === 'pipeline') {
      STEP_ORDER.forEach((id, i) => this.timers.push(setTimeout(() => this.litSteps.update(s => new Set([...s, id])), 600 + i * 300)));
    }
    if (scene === 'score') {
      if (!this.verdict()) this.verdict.set({ score: 889, tier: 'VeryLow', outcome: 'Approve', coveragePercent: 100, source: 'fallback' });
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

  edgePath(from: string, to: string): string {
    const a = this.stepAgent.get(from)!, b = this.stepAgent.get(to)!;
    if (a.x === b.x) return `M${a.x + 100} ${a.y} C ${a.x + 130} ${a.y}, ${a.x + 130} ${b.y}, ${b.x + 100} ${b.y}`;
    const mx = (a.x + 100 + b.x - 100) / 2;
    return `M${a.x + 100} ${a.y} C ${mx} ${a.y}, ${mx} ${b.y}, ${b.x - 100} ${b.y}`;
  }

  finish(): void {
    this.timers.forEach(clearTimeout);
    this.done.emit();
  }
}
