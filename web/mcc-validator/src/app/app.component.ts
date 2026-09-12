import { Component, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { BreakpointObserver } from '@angular/cdk/layout';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map, startWith } from 'rxjs';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';

interface NavItem { path: string; label: string; icon: string; blurb: string; }
interface NavGroup { title: string; items: NavItem[]; }

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatIconModule, MatButtonModule, MatTooltipModule],
  template: `
    <mat-sidenav-container class="shell" [hasBackdrop]="handset()">
      <mat-sidenav [mode]="handset() ? 'over' : 'side'" [opened]="handset() ? opened() : true" (closed)="opened.set(false)" class="nav" [fixedInViewport]="true">
        <a class="brand" routerLink="/assess" (click)="handset() && opened.set(false)">
          <span class="brand-mark"><mat-icon>insights</mat-icon></span>
          <span class="brand-text">
            <span class="brand-name">Merchant Intelligence</span>
            <span class="brand-sub">Onboarding &amp; underwriting</span>
          </span>
        </a>

        <nav class="nav-scroll">
          @for (g of groups; track g.title) {
            <div class="nav-group">
              <div class="nav-title">{{ g.title }}</div>
              @for (n of g.items; track n.path) {
                <a class="nav-item" [routerLink]="n.path" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: false }" (click)="handset() && opened.set(false)">
                  <mat-icon>{{ n.icon }}</mat-icon>
                  <span>{{ n.label }}</span>
                </a>
              }
            </div>
          }
        </nav>

        <div class="nav-footer">
          <a class="nav-item" href="/swagger" target="_blank" rel="noopener">
            <mat-icon>api</mat-icon><span>API reference</span><mat-icon class="ext">open_in_new</mat-icon>
          </a>
        </div>
      </mat-sidenav>

      <mat-sidenav-content>
        <header class="topbar">
          @if (handset()) {
            <button mat-icon-button (click)="opened.set(!opened())" aria-label="Toggle navigation"><mat-icon>menu</mat-icon></button>
          }
          <div class="topbar-text">
            <div class="crumbs">
              <span>{{ current().group }}</span>
              <mat-icon>chevron_right</mat-icon>
              <span class="crumb-current">{{ current().label }}</span>
            </div>
            <div class="blurb">{{ current().blurb }}</div>
          </div>
          <span class="spacer"></span>
          <span class="env-pill" matTooltip="All checks run against the local API on :5292"><span class="dot"></span>Local API</span>
        </header>
        <main class="content"><router-outlet></router-outlet></main>
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
  styles: [`
    :host { display: block; min-height: 100vh; }
    .shell { min-height: 100vh; }

    /* ---- Sidebar ---- */
    .nav {
      width: 264px; border-right: none;
      background: linear-gradient(180deg, var(--mi-nav-bg) 0%, var(--mi-nav-bg-2) 100%);
      color: var(--mi-nav-text);
      display: flex; flex-direction: column;
    }
    .brand { display: flex; align-items: center; gap: 12px; padding: 22px 20px 18px; text-decoration: none; color: inherit; }
    .brand:hover { text-decoration: none; }
    .brand-mark {
      width: 40px; height: 40px; border-radius: 12px; display: grid; place-items: center; flex: none;
      background: linear-gradient(135deg, #4f7cff, #8b5cf6);
      box-shadow: 0 8px 20px -6px rgba(79, 124, 255, 0.6);
      color: #fff;
    }
    .brand-text { display: flex; flex-direction: column; line-height: 1.2; }
    .brand-name { color: #fff; font-weight: 600; font-size: 15px; letter-spacing: -0.01em; }
    .brand-sub { font-size: 11.5px; color: var(--mi-nav-text); opacity: 0.8; margin-top: 2px; }

    .nav-scroll { flex: 1; min-height: 0; overflow-y: auto; padding: 4px 12px 12px; scrollbar-width: none; }
    .nav-scroll::-webkit-scrollbar { display: none; }
    .nav-group { margin-top: 14px; }
    .nav-title { font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.12em; color: #64748b; font-weight: 600; padding: 0 12px 8px; }
    .nav-item {
      display: flex; align-items: center; gap: 12px; padding: 9px 12px; margin: 2px 0; border-radius: 10px;
      color: var(--mi-nav-text); font-size: 13.5px; font-weight: 500; text-decoration: none;
      transition: background 120ms ease, color 120ms ease;
      position: relative;
    }
    .nav-item:hover { background: rgba(255, 255, 255, 0.06); color: #fff; text-decoration: none; }
    .nav-item mat-icon { font-size: 20px; width: 20px; height: 20px; opacity: 0.85; }
    .nav-item.active { background: rgba(79, 124, 255, 0.18); color: var(--mi-nav-active); }
    .nav-item.active mat-icon { color: #8fb0ff; opacity: 1; }
    .nav-item.active::before {
      content: ''; position: absolute; left: -12px; top: 8px; bottom: 8px; width: 3px; border-radius: 0 3px 3px 0;
      background: linear-gradient(180deg, #4f7cff, #8b5cf6);
    }
    .nav-item .ext { margin-left: auto; font-size: 15px; width: 15px; height: 15px; opacity: 0.6; }
    .nav-footer { padding: 12px; border-top: 1px solid rgba(255, 255, 255, 0.06); }

    /* ---- Top bar ---- */
    .topbar {
      position: sticky; top: 0; z-index: 5;
      display: flex; align-items: center; gap: 12px;
      padding: 14px 28px; min-height: 68px;
      background: rgba(243, 245, 249, 0.85); backdrop-filter: saturate(180%) blur(12px);
      border-bottom: 1px solid var(--mi-border);
    }
    .topbar-text { min-width: 0; }
    .crumbs { display: flex; align-items: center; gap: 4px; font-size: 12.5px; color: var(--mi-text-3); font-weight: 500; }
    .crumbs mat-icon { font-size: 16px; width: 16px; height: 16px; }
    .crumb-current { color: var(--mi-text); font-weight: 600; font-size: 15px; letter-spacing: -0.01em; }
    .blurb { font-size: 12.5px; color: var(--mi-text-2); margin-top: 2px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .spacer { flex: 1; }
    .env-pill {
      display: inline-flex; align-items: center; gap: 8px; padding: 6px 12px; border-radius: 999px;
      background: var(--mi-surface); border: 1px solid var(--mi-border); font-size: 12px; font-weight: 500; color: var(--mi-text-2);
      white-space: nowrap;
    }
    .env-pill .dot { width: 8px; height: 8px; border-radius: 50%; background: #22c55e; box-shadow: 0 0 0 3px rgba(34, 197, 94, 0.2); }

    .content { max-width: 1240px; margin: 0 auto; padding: 28px 28px 64px; }

    @media (max-height: 800px) {
      .brand { padding: 14px 20px 10px; }
      .nav-group { margin-top: 8px; }
      .nav-title { padding-bottom: 4px; }
      .nav-item { padding: 6px 12px; margin: 1px 0; font-size: 13px; }
      .nav-item.active::before { top: 6px; bottom: 6px; }
      .nav-footer { padding: 6px 12px; }
    }
    @media (max-height: 640px) {
      .brand { padding: 10px 20px 6px; }
      .brand-sub { display: none; }
      .nav-group { margin-top: 4px; }
      .nav-title { padding-bottom: 2px; }
      .nav-item { padding: 4px 12px; margin: 0; }
      .nav-item.active::before { top: 4px; bottom: 4px; }
    }

    @media (max-width: 900px) {
      .topbar { padding: 10px 14px; }
      .content { padding: 18px 14px 48px; }
      .blurb, .env-pill { display: none; }
    }
  `]
})
export class AppComponent {
  private readonly bp = inject(BreakpointObserver);
  private readonly router = inject(Router);
  readonly handset = toSignal(this.bp.observe('(max-width: 900px)').pipe(map(r => r.matches)), { initialValue: false });
  readonly opened = signal(false);

  readonly groups: NavGroup[] = [
    { title: 'Decision', items: [
      { path: '/assess', label: 'Full assessment', icon: 'checklist_rtl', blurb: 'One intake, every check, one decision with a PDF memo' },
      { path: '/score', label: 'Unified risk score', icon: 'speed', blurb: 'Blend credit, KYB, screening and web signals into a 0–1000 score' },
      { path: '/cases', label: 'Case queue', icon: 'inbox', blurb: 'Review, assign and decide merchant cases' },
      { path: '/rules', label: 'Policy rules', icon: 'rule', blurb: 'Versioned decision policy with history and rollback' },
      { path: '/workflows', label: 'Workflows', icon: 'account_tree', blurb: 'Enable, disable and reorder the checks an assessment runs' }
    ] },
    { title: 'Pre-boarding', items: [
      { path: '/kyb', label: 'KYB & screening', icon: 'verified_user', blurb: 'Registry verification, sanctions/PEP, adverse media, website compliance' },
      { path: '/mcc', label: 'MCC validator', icon: 'fact_check', blurb: 'Check the declared MCC against what the website actually sells' },
      { path: '/match', label: 'MATCH inquiry', icon: 'policy', blurb: 'Terminated Merchant File lookup' }
    ] },
    { title: 'Underwriting', items: [
      { path: '/underwriting', label: 'Explain, terms & statements', icon: 'account_balance', blurb: 'Model explanations, pricing terms and bank statement analysis' }
    ] },
    { title: 'Operations', items: [
      { path: '/audit', label: 'Audit trail', icon: 'history', blurb: 'Tamper-evident log of every decision and mutation' },
      { path: '/models', label: 'Model ops', icon: 'model_training', blurb: 'Registry, retraining, drift and champion/challenger' },
      { path: '/webhooks', label: 'Webhooks', icon: 'webhook', blurb: 'Signed event delivery to downstream systems' }
    ] }
  ];

  readonly current = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map(e => e.urlAfterRedirects),
      startWith(this.router.url),
      map(url => this.resolve(url))
    ),
    { initialValue: this.resolve(this.router.url) }
  );

  private resolve(url: string): { group: string; label: string; blurb: string } {
    const path = '/' + (url.split('?')[0].split('/')[1] ?? '');
    for (const g of this.groups) {
      const item = g.items.find(n => n.path === path);
      if (item) return { group: g.title, label: item.label, blurb: item.blurb };
    }
    return { group: 'Workbench', label: 'Merchant Intelligence', blurb: '' };
  }
}
