import { Component, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { BreakpointObserver } from '@angular/cdk/layout';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';

interface NavItem { path: string; label: string; icon: string; }
interface NavGroup { title: string; items: NavItem[]; }

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule, MatSidenavModule, MatListModule, MatIconModule, MatButtonModule, MatDividerModule],
  template: `
    <mat-toolbar color="primary" class="toolbar">
      @if (handset()) {
        <button mat-icon-button (click)="opened.set(!opened())" aria-label="Toggle navigation"><mat-icon>menu</mat-icon></button>
      }
      <mat-icon>insights</mat-icon>
      <span class="toolbar-title">Merchant Intelligence</span>
      <span class="spacer"></span>
      <span class="toolbar-subtitle">Onboarding &amp; underwriting workbench</span>
      <a mat-icon-button href="/swagger" target="_blank" rel="noopener" aria-label="Open Swagger"><mat-icon>api</mat-icon></a>
    </mat-toolbar>

    <mat-sidenav-container class="shell">
      <mat-sidenav [mode]="handset() ? 'over' : 'side'" [opened]="handset() ? opened() : true" (closed)="opened.set(false)" class="nav">
        <mat-nav-list>
          @for (g of groups; track g.title) {
            <div mat-subheader>{{ g.title }}</div>
            @for (n of g.items; track n.path) {
              <a mat-list-item [routerLink]="n.path" routerLinkActive="active" (click)="handset() && opened.set(false)">
                <mat-icon matListItemIcon>{{ n.icon }}</mat-icon>
                <span matListItemTitle>{{ n.label }}</span>
              </a>
            }
            <mat-divider></mat-divider>
          }
        </mat-nav-list>
      </mat-sidenav>
      <mat-sidenav-content>
        <main class="content"><router-outlet></router-outlet></main>
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; min-height: 100vh; background: #f4f6fa; }
    .toolbar { position: sticky; top: 0; z-index: 10; gap: 10px; }
    .toolbar-title { font-weight: 500; }
    .toolbar-subtitle { font-size: 13px; opacity: 0.85; }
    .spacer { flex: 1; }
    .shell { flex: 1; background: #f4f6fa; }
    .nav { width: 232px; border-right: 1px solid #e0e0e0; }
    .nav a.active { background: rgba(0, 86, 210, 0.10); font-weight: 500; }
    .content { max-width: 1200px; margin: 0 auto; padding: 24px 16px 48px; }
    [mat-subheader] { color: #666; font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; }
  `]
})
export class AppComponent {
  private readonly bp = inject(BreakpointObserver);
  readonly handset = toSignal(this.bp.observe('(max-width: 900px)').pipe(map(r => r.matches)), { initialValue: false });
  readonly opened = signal(false);

  readonly groups: NavGroup[] = [
    { title: 'Decision', items: [
      { path: '/score', label: 'Unified risk score', icon: 'speed' },
      { path: '/cases', label: 'Case queue', icon: 'inbox' },
      { path: '/rules', label: 'Policy rules', icon: 'rule' }
    ] },
    { title: 'Pre-boarding', items: [
      { path: '/kyb', label: 'KYB & screening', icon: 'verified_user' },
      { path: '/mcc', label: 'MCC validator', icon: 'fact_check' },
      { path: '/match', label: 'MATCH inquiry', icon: 'policy' }
    ] },
    { title: 'Underwriting', items: [
      { path: '/underwriting', label: 'Explain, terms & statements', icon: 'account_balance' }
    ] },
    { title: 'Operations', items: [
      { path: '/audit', label: 'Audit trail', icon: 'history' },
      { path: '/models', label: 'Model ops', icon: 'model_training' },
      { path: '/webhooks', label: 'Webhooks', icon: 'webhook' }
    ] }
  ];
}
