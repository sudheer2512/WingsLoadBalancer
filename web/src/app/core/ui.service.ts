import { Injectable, inject, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable, firstValueFrom } from 'rxjs';
import { ApiService } from './api.service';
import { AuthService, P, errorMessage, isConflict } from './auth.service';
import { Application, CompanyListItem, FailoverPolicy, Pool, RoutingPolicy, Server } from './models';
import { ConfirmData, ConfirmDialog, ConfirmResult, PromptData, PromptDialog } from '../shared/dialogs';

/** Snackbars and confirmation/prompt dialogs shared by every screen. */
@Injectable({ providedIn: 'root' })
export class UiService {
  private snack = inject(MatSnackBar);
  private dialog = inject(MatDialog);

  ok(message: string) { this.snack.open(message, undefined, { duration: 3000 }); }

  error(err: unknown) {
    const msg = isConflict(err) && !/reload/i.test(errorMessage(err))
      ? `${errorMessage(err)} — reload to see the latest version.` : errorMessage(err);
    this.snack.open(msg, 'Dismiss', { duration: 10000, panelClass: 'snack-error' });
  }

  /** Resolves to the result (with optional reason) when confirmed, undefined when cancelled. */
  confirm(data: ConfirmData): Promise<ConfirmResult | undefined> {
    return firstValueFrom(this.dialog.open(ConfirmDialog, { data, width: '480px', maxWidth: '95vw' }).afterClosed());
  }

  prompt(data: PromptData): Promise<string | undefined> {
    return firstValueFrom(this.dialog.open(PromptDialog, { data, width: '460px', maxWidth: '95vw' }).afterClosed());
  }

  /** Runs an API call, shows success/error toasts, and returns the result (undefined on error). */
  async run<T>(call: Observable<T>, success?: string): Promise<T | undefined> {
    try {
      const r = await firstValueFrom(call, { defaultValue: undefined as T });
      if (success) this.ok(success);
      return r ?? (true as unknown as T);
    } catch (e) {
      this.error(e);
      return undefined;
    }
  }
}

/** Cached reference data used by pickers (servers, companies, applications, pools, policies). */
@Injectable({ providedIn: 'root' })
export class LookupService {
  private api = inject(ApiService);
  private auth = inject(AuthService);
  readonly servers = signal<Server[]>([]);
  readonly companies = signal<CompanyListItem[]>([]);
  readonly applications = signal<Application[]>([]);
  readonly pools = signal<Pool[]>([]);
  readonly failoverPolicies = signal<FailoverPolicy[]>([]);
  readonly routingPolicies = signal<RoutingPolicy[]>([]);
  private loaded = new Set<string>();

  /** Loads each list once (or again with force); lists the admin may not read stay empty. */
  async ensure(kinds: ('servers' | 'companies' | 'applications' | 'pools' | 'failoverPolicies' | 'routingPolicies')[], force = false) {
    await Promise.all(kinds.map(async k => {
      if (this.loaded.has(k) && !force) return;
      try {
        switch (k) {
          case 'servers': if (this.auth.can(P.serversRead)) this.servers.set(await firstValueFrom(this.api.servers())); break;
          case 'companies': if (this.auth.can(P.companiesRead)) this.companies.set(await firstValueFrom(this.api.companies())); break;
          case 'applications': if (this.auth.can(P.serversRead)) this.applications.set(await firstValueFrom(this.api.applications())); break;
          case 'pools': if (this.auth.can(P.poolsRead)) this.pools.set(await firstValueFrom(this.api.pools())); break;
          case 'failoverPolicies': if (this.auth.can(P.poolsRead)) this.failoverPolicies.set(await firstValueFrom(this.api.failoverPolicies())); break;
          case 'routingPolicies': if (this.auth.can(P.poolsRead)) this.routingPolicies.set(await firstValueFrom(this.api.routingPolicies())); break;
        }
        this.loaded.add(k);
      } catch { /* pickers simply stay empty */ }
    }));
  }

  invalidate(...kinds: string[]) { for (const k of kinds) this.loaded.delete(k); }
  clear() { this.loaded.clear(); }

  serverName(id?: string | null) { return id ? this.servers().find(s => s.id === id)?.name ?? id.slice(0, 8) : ''; }
  companyCode(id?: string | null) { return id ? this.companies().find(c => c.id === id)?.code ?? id.slice(0, 8) : ''; }
  appCode(id?: string | null) { return id ? this.applications().find(a => a.id === id)?.code ?? id.slice(0, 8) : ''; }
  poolName(id?: string | null) { return id ? this.pools().find(p => p.id === id)?.name ?? id.slice(0, 8) : ''; }
}

// ---------------------------------------------------------------- date helpers for <input type="datetime-local">

/** ISO → "yyyy-MM-ddTHH:mm" in local time. */
export function toLocalInput(iso?: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** "yyyy-MM-ddTHH:mm" local → ISO (UTC) or null when empty. */
export function fromLocalInput(v?: string | null): string | null {
  if (!v) return null;
  const d = new Date(v);
  return isNaN(d.getTime()) ? null : d.toISOString();
}
