import { Injectable, inject, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService, P } from './auth.service';
import { DashboardSummary, LiveDecision, LiveHealth } from './models';

/** SignalR connection to /hubs/monitoring (cookie-authenticated). Started by the shell for monitoring.read users. */
@Injectable({ providedIn: 'root' })
export class MonitoringService {
  private auth = inject(AuthService);
  private connection?: signalR.HubConnection;
  private retry?: ReturnType<typeof setTimeout>;
  readonly connected = signal(false);
  readonly summary = signal<DashboardSummary | null>(null);
  /** Most recent decisions first, capped. */
  readonly decisions = signal<LiveDecision[]>([]);
  readonly decisions$ = new Subject<LiveDecision[]>();
  readonly health$ = new Subject<LiveHealth[]>();

  start() {
    if (this.connection || !this.auth.can(P.monitoringRead)) return;
    const c = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/monitoring')
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ctx => Math.min(30_000, 1_000 * 2 ** Math.min(ctx.previousRetryCount, 5)) })
      .configureLogging(signalR.LogLevel.Warning)
      .build();
    c.on('summary', (s: DashboardSummary) => this.summary.set(s));
    c.on('decisions', (rows: LiveDecision[]) => {
      this.decisions.update(cur => [...[...rows].reverse(), ...cur].slice(0, 200));
      this.decisions$.next(rows);
    });
    c.on('health', (rows: LiveHealth[]) => this.health$.next(rows));
    c.onreconnected(() => this.connected.set(true));
    c.onreconnecting(() => this.connected.set(false));
    c.onclose(() => { this.connected.set(false); this.schedule(); });
    this.connection = c;
    this.connect();
  }

  private connect() {
    const c = this.connection;
    if (!c || c.state !== signalR.HubConnectionState.Disconnected) return;
    c.start().then(() => this.connected.set(true)).catch(() => { this.connected.set(false); this.schedule(); });
  }

  private schedule() {
    clearTimeout(this.retry);
    if (this.connection) this.retry = setTimeout(() => this.connect(), 5_000);
  }

  stop() {
    clearTimeout(this.retry);
    const c = this.connection;
    this.connection = undefined;
    c?.stop();
    this.connected.set(false);
    this.decisions.set([]);
    this.summary.set(null);
  }
}
