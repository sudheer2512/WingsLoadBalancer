import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, firstValueFrom, throwError } from 'rxjs';
import { ApiService } from './api.service';
import { ApiError, Me } from './models';

/** Permission codes (mirror of Wings.LoadBalancer.Data.Permissions). */
export const P = {
  serversRead: 'servers.read', serversWrite: 'servers.write', serversOperate: 'servers.operate',
  companiesRead: 'companies.read', companiesWrite: 'companies.write',
  assignmentsRead: 'assignments.read', assignmentsWrite: 'assignments.write',
  poolsRead: 'pools.read', poolsWrite: 'pools.write', policiesWrite: 'policies.write',
  routingSimulate: 'routing.simulate', monitoringRead: 'monitoring.read', auditRead: 'audit.read',
  alertsRead: 'alerts.read', alertsManage: 'alerts.manage', reportsRead: 'reports.read',
  sessionsRead: 'sessions.read', sessionsRevoke: 'sessions.revoke', settingsWrite: 'settings.write',
  adminsManage: 'admins.manage', secretsExport: 'secrets.export',
} as const;

/**
 * Cookie-session state. The session cookie is HttpOnly; the client only knows what /api/auth/me reports.
 * Every GET under /api refreshes the XSRF-TOKEN cookie, which Angular echoes back as X-XSRF-TOKEN on unsafe requests.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private api = inject(ApiService);
  private router = inject(Router);
  readonly me = signal<Me>({ authenticated: false });
  readonly authenticated = computed(() => this.me().authenticated);
  readonly permissions = computed(() => new Set(this.me().permissions ?? []));

  can(permission: string) { return this.permissions().has(permission); }
  canAny(...permissions: string[]) { return permissions.some(p => this.permissions().has(p)); }

  /** Called once at start-up: reads the current session (and obtains the XSRF cookie). */
  async init() {
    try { this.me.set(await firstValueFrom(this.api.me())); }
    catch { this.me.set({ authenticated: false }); }
  }

  /** Returns true when signed in, false when an MFA code is required. Throws on bad credentials. */
  async login(username: string, password: string, mfaCode?: string): Promise<boolean> {
    await firstValueFrom(this.api.csrf());
    const result = await firstValueFrom(this.api.login(username, password, mfaCode));
    if (result.mfaRequired) return false;
    // /me re-issues the antiforgery token for the new identity; the old token is bound to the anonymous user.
    this.me.set(await firstValueFrom(this.api.me()));
    return this.me().authenticated;
  }

  async refresh() {
    try { this.me.set(await firstValueFrom(this.api.me())); } catch { /* keep current state */ }
  }

  async logout() {
    try { await firstValueFrom(this.api.logout()); } catch { /* already signed out */ }
    this.signedOut();
    try { await firstValueFrom(this.api.csrf()); } catch { /* ignore */ }
  }

  /** Local sign-out after a 401 (session expired or account disabled). */
  signedOut() {
    this.me.set({ authenticated: false });
    const current = this.router.url;
    if (!current.startsWith('/login')) this.router.navigate(['/login'], { queryParams: current && current !== '/' ? { returnUrl: current } : {} });
  }
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const snack = inject(MatSnackBar);
  return next(req).pipe(catchError((err: HttpErrorResponse) => {
    const isAuthCall = req.url.startsWith('/api/auth/');
    if (err.status === 401 && !isAuthCall) auth.signedOut();
    if (err.status === 403) snack.open("You don't have permission to do that.", 'Dismiss', { duration: 6000, panelClass: 'snack-error' });
    return throwError(() => err);
  }));
};

export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  return auth.authenticated() ? true : inject(Router).createUrlTree(['/login'], { queryParams: state.url && state.url !== '/' ? { returnUrl: state.url } : {} });
};

/** Route guard for screens that need a permission; shows the "no permission" page otherwise. */
export const permissionGuard: CanActivateFn = route => {
  const needed = (route.data?.['perm'] as string | string[] | undefined) ?? [];
  const list = Array.isArray(needed) ? needed : [needed];
  const auth = inject(AuthService);
  return list.length === 0 || auth.canAny(...list) ? true : inject(Router).createUrlTree(['/forbidden']);
};

/** Extracts a readable message from the API's {error, message, errors} responses. */
export function errorMessage(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as ApiError | string | null;
    if (body && typeof body === 'object') {
      if (body.errors?.length) return body.errors.join(' • ');
      if (body.message) return body.message;
      if (body.error === 'csrf_token_invalid') return 'Your security token expired. Reload the page and try again.';
      if (body.error) return humanizeCode(body.error);
    }
    if (typeof body === 'string' && body.length < 300 && body.trim()) return body;
    if (err.status === 0) return 'The server could not be reached.';
    if (err.status === 401) return 'Your session has ended. Sign in again.';
    if (err.status === 403) return "You don't have permission to do that.";
    if (err.status === 404) return 'Not found. It may have been deleted.';
    if (err.status === 409) return 'The record was changed by someone else. Reload and try again.';
    return `${err.status} ${err.statusText}`;
  }
  return err instanceof Error ? err.message : String(err);
}

/** Validation errors as a list (empty when the error is not a validation failure). */
export function errorList(err: unknown): string[] {
  if (err instanceof HttpErrorResponse && err.error && typeof err.error === 'object') {
    const b = err.error as ApiError;
    if (b.errors?.length) return b.errors;
    if (b.blockers?.length) return b.blockers;
  }
  return [];
}

export function isConflict(err: unknown) { return err instanceof HttpErrorResponse && err.status === 409; }

function humanizeCode(code: string) {
  const s = code.replace(/_/g, ' ');
  return s.charAt(0).toUpperCase() + s.slice(1);
}
