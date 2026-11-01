import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatBadgeModule } from '@angular/material/badge';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { firstValueFrom, map } from 'rxjs';
import { ApiService } from './core/api.service';
import { AuthService, P, errorMessage } from './core/auth.service';
import { MonitoringService } from './core/monitoring.service';
import { LookupService } from './core/ui.service';

interface NavItem { label: string; icon: string; link: string; perm: string; exact?: boolean; }
interface NavGroup { title: string; items: NavItem[]; }

const NAV: NavGroup[] = [
  { title: 'Monitor', items: [
    { label: 'Dashboard', icon: 'dashboard', link: '/dashboard', perm: P.monitoringRead },
    { label: 'Alerts', icon: 'notifications_active', link: '/alerts', perm: P.alertsRead },
    { label: 'Active sessions', icon: 'groups', link: '/sessions', perm: P.sessionsRead },
  ] },
  { title: 'Infrastructure', items: [
    { label: 'Servers', icon: 'dns', link: '/servers', perm: P.serversRead },
    { label: 'Server pools', icon: 'hub', link: '/pools', perm: P.poolsRead },
    { label: 'Maintenance', icon: 'build', link: '/maintenance', perm: P.serversRead },
  ] },
  { title: 'Tenants', items: [
    { label: 'Companies', icon: 'apartment', link: '/companies', perm: P.companiesRead },
    { label: 'User assignments', icon: 'manage_accounts', link: '/users', perm: P.companiesRead, exact: true },
    { label: 'Bulk upload', icon: 'upload_file', link: '/users/bulk', perm: P.assignmentsWrite },
  ] },
  { title: 'Routing', items: [
    { label: 'Policies & applications', icon: 'policy', link: '/policies', perm: P.poolsRead },
    { label: 'Routing simulator', icon: 'alt_route', link: '/routing/simulator', perm: P.routingSimulate },
    { label: 'Routing history', icon: 'history', link: '/routing/history', perm: P.monitoringRead },
    { label: 'Configuration', icon: 'fact_check', link: '/routing/config', perm: P.monitoringRead },
  ] },
  { title: 'Governance', items: [
    { label: 'Audit log', icon: 'receipt_long', link: '/audit', perm: P.auditRead },
    { label: 'Reports', icon: 'summarize', link: '/reports', perm: P.reportsRead },
    { label: 'Settings', icon: 'tune', link: '/settings', perm: P.monitoringRead },
    { label: 'Administrators', icon: 'admin_panel_settings', link: '/admins', perm: P.adminsManage },
  ] },
];

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatListModule, MatButtonModule, MatIconModule, MatMenuModule,
    MatTooltipModule, MatBadgeModule, MatDialogModule],
  template: `
    <header class="topbar">
      <button mat-icon-button class="menu-btn" (click)="navOpen.set(!navOpen())" aria-label="Toggle navigation"><mat-icon>menu</mat-icon></button>
      <a class="brand" routerLink="/"><mat-icon>alt_route</mat-icon><span>Wings Load Balancer</span><small>Administration</small></a>
      <span class="spacer"></span>
      @if (auth.can(P.monitoringRead)) {
        <span class="live" [class.on]="monitoring.connected()" role="status"
              [matTooltip]="monitoring.connected() ? 'Live monitoring connected' : 'Live monitoring offline (retrying)'">
          <span class="dot"></span>{{ monitoring.connected() ? 'Live' : 'Offline' }}
        </span>
      }
      @if (auth.can(P.alertsRead)) {
        <a mat-icon-button routerLink="/alerts" aria-label="Open alerts" matTooltip="Open alerts"
           [matBadge]="openAlerts() || null" matBadgeColor="warn" matBadgeSize="small"><mat-icon>notifications</mat-icon></a>
      }
      <button mat-button [matMenuTriggerFor]="userMenu" class="user"><mat-icon>account_circle</mat-icon><span class="uname">{{ auth.me().displayName }}</span></button>
      <mat-menu #userMenu xPosition="before">
        <div class="menu-head">{{ auth.me().displayName }}<br><small>{{ auth.me().username }} · MFA {{ auth.me().mfaEnabled ? 'on' : 'off' }}</small></div>
        <button mat-menu-item (click)="changePassword()"><mat-icon>password</mat-icon>Change password</button>
        <button mat-menu-item (click)="setupMfa()"><mat-icon>phonelink_lock</mat-icon>{{ auth.me().mfaEnabled ? 'Reset' : 'Set up' }} two-factor authentication</button>
        <a mat-menu-item href="/api/docs" target="_blank" rel="noopener"><mat-icon>description</mat-icon>API documentation</a>
        <button mat-menu-item (click)="signOut()"><mat-icon>logout</mat-icon>Sign out</button>
      </mat-menu>
    </header>
    <mat-sidenav-container class="container">
      <mat-sidenav [mode]="wide() ? 'side' : 'over'" [opened]="navOpen()" (openedChange)="navOpen.set($event)" class="nav">
        <nav aria-label="Main">
          @for (g of nav(); track g.title) {
            <div class="group">{{ g.title }}</div>
            @for (i of g.items; track i.link) {
              <a class="nav-item" [routerLink]="i.link" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: !!i.exact }" (click)="closeIfNarrow()">
                <mat-icon>{{ i.icon }}</mat-icon><span>{{ i.label }}</span>
              </a>
            }
          }
        </nav>
      </mat-sidenav>
      <mat-sidenav-content class="content"><router-outlet /></mat-sidenav-content>
    </mat-sidenav-container>`,
  styles: `
    :host { display: flex; flex-direction: column; height: 100vh; }
    .topbar { display: flex; align-items: center; gap: 8px; height: 48px; padding: 0 8px; background: var(--chrome); color: var(--chrome-fg); flex: none; z-index: 3; }
    .topbar .mat-mdc-icon-button, .user { color: var(--chrome-fg) !important; }
    .brand { display: flex; align-items: center; gap: 6px; font-weight: 600; color: inherit; text-decoration: none; white-space: nowrap; }
    .brand small { color: var(--chrome-muted); font-weight: 400; margin-left: 4px; }
    .spacer { flex: 1; }
    .live { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; color: var(--chrome-muted); padding: 0 8px; }
    .live .dot { width: 8px; height: 8px; border-radius: 50%; background: #8b929c; }
    .live.on { color: var(--chrome-fg); } .live.on .dot { background: #3fb950; }
    .menu-head { padding: 8px 16px; font-weight: 600; } .menu-head small { font-weight: 400; color: var(--muted); }
    .container { flex: 1; min-height: 0; background: var(--canvas); }
    .nav { width: 232px; background: var(--surface); border-right: 1px solid var(--border); }
    nav { padding: 8px 0 24px; }
    .group { font-size: 11px; text-transform: uppercase; letter-spacing: .06em; color: var(--muted); padding: 14px 18px 4px; }
    .nav-item { display: flex; align-items: center; gap: 12px; padding: 7px 18px; color: var(--text); text-decoration: none; border-left: 3px solid transparent; }
    .nav-item mat-icon { font-size: 20px; width: 20px; height: 20px; color: var(--muted); }
    .nav-item:hover { background: var(--hover); }
    .nav-item.active { background: var(--selected); border-left-color: var(--accent); font-weight: 600; }
    .nav-item.active mat-icon { color: var(--accent); }
    .content { height: 100%; overflow: hidden; }
    @media (max-width: 720px) { .brand small, .uname { display: none; } }
  `,
})
export class Shell implements OnInit, OnDestroy {
  readonly P = P;
  auth = inject(AuthService);
  monitoring = inject(MonitoringService);
  private lookups = inject(LookupService);
  private dialog = inject(MatDialog);
  private router = inject(Router);
  wide = toSignal(inject(BreakpointObserver).observe('(min-width: 1100px)').pipe(map(r => r.matches)), { initialValue: true });
  navOpen = signal(true);
  nav = signal<NavGroup[]>([]);
  openAlerts = () => this.monitoring.summary()?.openAlerts ?? 0;

  ngOnInit() {
    this.nav.set(NAV.map(g => ({ ...g, items: g.items.filter(i => this.auth.can(i.perm)) })).filter(g => g.items.length));
    this.navOpen.set(this.wide());
    this.monitoring.start();
  }

  ngOnDestroy() { this.monitoring.stop(); }

  closeIfNarrow() { if (!this.wide()) this.navOpen.set(false); }

  async signOut() {
    this.monitoring.stop();
    this.lookups.clear();
    await this.auth.logout();
    this.router.navigate(['/login']);
  }

  changePassword() { this.dialog.open(ChangePasswordDialog, { width: '420px', maxWidth: '95vw' }); }
  setupMfa() { this.dialog.open(MfaDialog, { width: '520px', maxWidth: '95vw' }); }
}

@Component({
  selector: 'app-change-password',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, FormsModule],
  template: `
    <h2 mat-dialog-title>Change password</h2>
    <form (ngSubmit)="save()">
      <mat-dialog-content>
        <mat-form-field class="full"><mat-label>Current password</mat-label><input matInput type="password" name="c" [(ngModel)]="current" autocomplete="current-password" required /></mat-form-field>
        <mat-form-field class="full"><mat-label>New password</mat-label><input matInput type="password" name="n" [(ngModel)]="next" autocomplete="new-password" required minlength="12" />
          <mat-hint>At least 12 characters.</mat-hint></mat-form-field>
        <mat-form-field class="full"><mat-label>Confirm new password</mat-label><input matInput type="password" name="r" [(ngModel)]="repeat" autocomplete="new-password" required /></mat-form-field>
        @if (repeat && repeat !== next) { <p class="error">The passwords do not match.</p> }
        @if (error()) { <p class="error">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button [disabled]="busy() || !current || next.length < 12 || next !== repeat">Change password</button>
      </mat-dialog-actions>
    </form>`,
})
export class ChangePasswordDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<ChangePasswordDialog>);
  current = ''; next = ''; repeat = '';
  busy = signal(false);
  error = signal('');
  async save() {
    this.busy.set(true); this.error.set('');
    try {
      await firstValueFrom(this.api.changePassword(this.current, this.next));
      this.ref.close(true);
    } catch (e) {
      const msg = errorMessage(e);
      this.error.set(msg === 'Invalid password' ? 'The current password is incorrect.' : msg);
    } finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'app-mfa-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatIconModule, FormsModule],
  template: `
    <h2 mat-dialog-title>Two-factor authentication</h2>
    <mat-dialog-content>
      @if (done()) {
        <div class="callout good">Two-factor authentication is now enabled. You will be asked for a code at your next sign-in.</div>
      } @else if (!uri()) {
        <p>Generate a new secret for an authenticator app (Microsoft Authenticator, Google Authenticator, 1Password …).</p>
        @if (auth.me().mfaEnabled) {
          <div class="callout warn">Two-factor authentication is on. Enter a code from your current authenticator to re-enrol; sign-in uses the new secret only after you confirm a code from it.</div>
          <mat-form-field class="full"><mat-label>Current 6-digit code</mat-label>
            <input matInput [(ngModel)]="currentCode" inputmode="numeric" autocomplete="one-time-code" maxlength="8" /></mat-form-field>
        }
      } @else {
        <p>Add this account to your authenticator app using the setup URI below (paste it into the app or a QR generator you trust), then enter the 6-digit code it shows.</p>
        <div class="uri-box"><code class="mono">{{ uri() }}</code>
          <button mat-icon-button type="button" (click)="copy()" aria-label="Copy setup URI"><mat-icon>content_copy</mat-icon></button></div>
        @if (secret()) { <p class="muted small">Manual entry key: <code class="mono">{{ secret() }}</code></p> }
        <mat-form-field class="full"><mat-label>6-digit code</mat-label>
          <input matInput [(ngModel)]="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" (keydown.enter)="enable()" /></mat-form-field>
      }
      @if (error()) { <p class="error">{{ error() }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ done() ? 'Close' : 'Cancel' }}</button>
      @if (!done()) {
        @if (!uri()) { <button mat-flat-button [disabled]="busy()" (click)="start()">Generate secret</button> }
        @else { <button mat-flat-button [disabled]="busy() || code.trim().length < 6" (click)="enable()">Enable</button> }
      }
    </mat-dialog-actions>`,
  styles: `.uri-box { display: flex; align-items: center; gap: 4px; padding: 6px 8px; background: var(--surface-2); border: 1px solid var(--border); border-radius: 4px; margin-bottom: 10px; }
    .uri-box code { flex: 1; word-break: break-all; }`,
})
export class MfaDialog {
  auth = inject(AuthService);
  private api = inject(ApiService);
  uri = signal('');
  secret = signal('');
  code = '';
  currentCode = '';
  busy = signal(false);
  done = signal(false);
  error = signal('');

  async start() {
    this.busy.set(true); this.error.set('');
    try {
      const r = await firstValueFrom(this.api.mfaSetup(this.currentCode.trim() || null));
      this.uri.set(r.provisioningUri);
      try { this.secret.set(new URL(r.provisioningUri).searchParams.get('secret') ?? ''); } catch { /* not a URL */ }
      await this.auth.refresh();
    } catch (e) { this.error.set(errorMessage(e)); } finally { this.busy.set(false); }
  }

  async enable() {
    if (this.code.trim().length < 6) return;
    this.busy.set(true); this.error.set('');
    try {
      await firstValueFrom(this.api.mfaEnable(this.code.trim()));
      this.done.set(true);
      await this.auth.refresh();
    } catch (e) {
      const m = errorMessage(e);
      this.error.set(m === 'Invalid code' ? 'That code is not valid. Check the time on your device and try again.' : m);
    } finally { this.busy.set(false); }
  }

  copy() { navigator.clipboard?.writeText(this.uri()).catch(() => undefined); }
}

@Component({
  selector: 'app-login',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule],
  template: `
    <div class="wrap">
      <form class="card" (ngSubmit)="submit()">
        <div class="title"><mat-icon>alt_route</mat-icon><h1>Wings Load Balancer</h1></div>
        <p class="muted">Administration console — sign in with your administrator account.</p>
        @if (!mfa()) {
          <mat-form-field class="full"><mat-label>Username</mat-label><input matInput name="u" [(ngModel)]="username" autocomplete="username" required /></mat-form-field>
          <mat-form-field class="full"><mat-label>Password</mat-label><input matInput name="p" type="password" [(ngModel)]="password" autocomplete="current-password" required /></mat-form-field>
        } @else {
          <p>Enter the 6-digit code from your authenticator app for <strong>{{ username }}</strong>.</p>
          <mat-form-field class="full"><mat-label>Authentication code</mat-label>
            <input matInput name="m" [(ngModel)]="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" required /></mat-form-field>
        }
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        <button mat-flat-button class="full" [disabled]="busy() || !username || !password || (mfa() && !code)">{{ mfa() ? 'Verify' : 'Sign in' }}</button>
        @if (mfa()) { <button mat-button type="button" class="full back" (click)="reset()">Use a different account</button> }
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      </form>
    </div>`,
  styles: `
    .wrap { display: grid; place-items: center; min-height: 100vh; background: var(--canvas); padding: 16px; }
    .card { width: min(400px, 100%); background: var(--surface); padding: 28px; border-radius: 10px; border: 1px solid var(--border); box-shadow: 0 6px 24px rgba(0,0,0,.08); }
    .title { display: flex; align-items: center; gap: 8px; color: var(--accent); } h1 { font-size: 20px; margin: 0; color: var(--text); }
    .back { margin-top: 6px; }
  `,
})
export class Login implements OnInit {
  private auth = inject(AuthService);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  private lookups = inject(LookupService);
  username = '';
  password = '';
  code = '';
  mfa = signal(false);
  busy = signal(false);
  error = signal('');

  ngOnInit() { if (this.auth.authenticated()) this.router.navigateByUrl('/'); }

  reset() { this.mfa.set(false); this.code = ''; this.password = ''; this.error.set(''); }

  async submit() {
    this.busy.set(true);
    this.error.set('');
    try {
      const ok = await this.auth.login(this.username, this.password, this.mfa() ? this.code : undefined);
      if (!ok) { this.mfa.set(true); return; }
      this.password = '';
      this.lookups.clear();
      const ret = this.route.snapshot.queryParamMap.get('returnUrl');
      this.router.navigateByUrl(ret && ret.startsWith('/') && !ret.startsWith('//') ? ret : '/');
    } catch (e) {
      const status = (e as { status?: number }).status;
      this.error.set(status === 429 ? 'Too many sign-in attempts. Wait a minute and try again.' : errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'app-forbidden',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule, RouterLink],
  template: `
    <div class="page"><div class="panel box">
      <mat-icon>lock</mat-icon>
      <h1>You don't have permission</h1>
      <p class="muted">Your administrator role does not include access to this screen. Ask a super administrator to grant the required permission.</p>
      <a routerLink="/">Go to the start page</a>
    </div></div>`,
  styles: `.box { max-width: 480px; margin: 48px auto; text-align: center; padding: 32px; } mat-icon { font-size: 40px; width: 40px; height: 40px; color: var(--muted); }`,
})
export class Forbidden {}

