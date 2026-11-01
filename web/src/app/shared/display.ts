import { ChangeDetectionStrategy, Component, Pipe, PipeTransform, computed, input, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { PageEvent } from '@angular/material/paginator';
import { Sort } from '@angular/material/sort';

export type Tone = 'good' | 'warn' | 'bad' | 'maint' | 'off' | 'info';

const TONES: Record<string, Tone> = {
  HEALTHY: 'good', ONLINE: 'good', ACTIVE: 'good', ROUTED: 'good', CURRENT: 'good', ENABLED: 'good', RESOLVED: 'good', PASSED: 'good',
  YES: 'good', UP: 'good', INSYNC: 'good', SUCCESS: 'good', VALID: 'good',
  DEGRADED: 'warn', RECOVERING: 'warn', SUSPENDED: 'warn', WARNING: 'warn', OPEN: 'warn', OUTOFSYNC: 'warn', STALE: 'warn',
  UNHEALTHY: 'bad', OFFLINE: 'bad', DISABLED: 'bad', DENIED: 'bad', NOASSIGNMENT: 'bad', NOHEALTHYDESTINATION: 'bad',
  CONFIGURATIONSTALE: 'bad', CRITICAL: 'bad', FAILED: 'bad', ERROR: 'bad', INVALID: 'bad', DOWN: 'bad', NO: 'bad',
  MAINTENANCE: 'maint', DRAINING: 'maint', INMAINTENANCE: 'maint',
  UNKNOWN: 'off', RETIRED: 'off', ENDED: 'off', CANCELLED: 'off', COMPLETED: 'off', NONE: 'off', NOTFOUND: 'off',
  SCHEDULED: 'info', INFO: 'info', ACKNOWLEDGED: 'info', NEW: 'info',
};
const ICONS: Record<Tone, string> = { good: 'check_circle', warn: 'warning', bad: 'error', maint: 'build', off: 'help_outline', info: 'info' };

export function toneFor(v: string | null | undefined): Tone {
  return TONES[(v ?? '').replace(/[\s_-]/g, '').toUpperCase()] ?? 'off';
}

/** Status chip: icon + text + colour, so the state is never conveyed by colour alone. */
@Component({
  selector: 'app-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule],
  template: `<span class="chip" [attr.data-tone]="tone()" [attr.title]="title() || null"><mat-icon aria-hidden="true">{{ icon() }}</mat-icon>{{ text() }}</span>`,
})
export class StatusChip {
  value = input<string | null | undefined>('');
  label = input<string | null | undefined>(undefined);
  /** Override the tone derived from the value. */
  forceTone = input<Tone | undefined>(undefined, { alias: 'tone' });
  title = input<string | null | undefined>(undefined);
  tone = computed(() => this.forceTone() ?? toneFor(this.value()));
  icon = computed(() => ICONS[this.tone()]);
  text = computed(() => this.label() ?? humanize(this.value()));
}

export function humanize(v: string | null | undefined): string {
  if (!v) return '';
  const spaced = v.replace(/_/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z])([A-Z][a-z])/g, '$1 $2');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

/** "WeightedLeastRequests" → "Weighted least requests". */
@Pipe({ name: 'humanize' })
export class HumanizePipe implements PipeTransform {
  transform(v: string | null | undefined): string { return humanize(v); }
}

/** Relative time ("3 min ago", "in 2 h"). */
@Pipe({ name: 'ago' })
export class AgoPipe implements PipeTransform {
  transform(v: string | null | undefined): string {
    if (!v) return '—';
    const t = new Date(v).getTime();
    if (isNaN(t)) return '—';
    const s = Math.round((Date.now() - t) / 1000);
    const abs = Math.abs(s);
    const unit = abs < 60 ? `${abs} s` : abs < 3600 ? `${Math.round(abs / 60)} min` : abs < 86400 ? `${Math.round(abs / 3600)} h` : `${Math.round(abs / 86400)} d`;
    return s >= 0 ? `${unit} ago` : `in ${unit}`;
  }
}

/** Number formatting that renders null as an em dash. */
@Pipe({ name: 'n' })
export class NumPipe implements PipeTransform {
  transform(v: number | null | undefined, digits = 0, suffix = ''): string {
    if (v === null || v === undefined || isNaN(v)) return '—';
    return v.toLocaleString(undefined, { maximumFractionDigits: digits, minimumFractionDigits: 0 }) + suffix;
  }
}

/** Small CPU/memory style bar with the value as text. */
@Component({
  selector: 'app-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="meter" [attr.data-level]="level()" [attr.aria-label]="label() + ' ' + text()">
      <span class="track"><span class="fill" [style.width.%]="pct()"></span></span><span class="txt">{{ text() }}</span></span>`,
  styles: `
    .meter { display: inline-flex; align-items: center; gap: 6px; min-width: 96px; }
    .track { flex: 1; height: 6px; border-radius: 3px; background: var(--grid); overflow: hidden; min-width: 48px; }
    .fill { display: block; height: 100%; background: var(--series-1); border-radius: 3px; }
    [data-level='warn'] .fill { background: var(--warn); } [data-level='bad'] .fill { background: var(--bad); }
    .txt { font-variant-numeric: tabular-nums; min-width: 36px; text-align: right; font-size: 12px; }
  `,
})
export class Meter {
  value = input<number | null | undefined>(null);
  label = input('');
  warnAt = input(75);
  badAt = input(90);
  pct = computed(() => Math.max(0, Math.min(100, this.value() ?? 0)));
  text = computed(() => this.value() == null ? '—' : `${Math.round(this.value()!)}%`);
  level = computed(() => (this.value() ?? 0) >= this.badAt() ? 'bad' : (this.value() ?? 0) >= this.warnAt() ? 'warn' : 'ok');
}

/**
 * Client-side filter/sort/page state for mat-table grids, built on signals.
 * Use: [dataSource]="g.view()", matSort (matSortChange)="g.sortBy($event)", mat-paginator (page)="g.pageBy($event)".
 */
export class Grid<T> {
  readonly rows = signal<T[]>([]);
  readonly filter = signal('');
  readonly sort = signal<Sort>({ active: '', direction: '' });
  readonly pageIndex = signal(0);
  readonly pageSize = signal(25);
  readonly loading = signal(false);

  constructor(
    private text: (row: T) => string = row => JSON.stringify(row),
    private value: (row: T, column: string) => unknown = (row, c) => (row as Record<string, unknown>)[c],
    pageSize = 25,
  ) { this.pageSize.set(pageSize); }

  readonly filtered = computed(() => {
    const f = this.filter().trim().toLowerCase();
    const rows = f ? this.rows().filter(r => this.text(r).toLowerCase().includes(f)) : this.rows();
    const { active, direction } = this.sort();
    if (!active || !direction) return rows;
    const dir = direction === 'asc' ? 1 : -1;
    return [...rows].sort((a, b) => compare(this.value(a, active), this.value(b, active)) * dir);
  });
  readonly count = computed(() => this.filtered().length);
  readonly view = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    return this.filtered().slice(start, start + this.pageSize());
  });

  set(rows: T[]) { this.rows.set(rows); if (this.pageIndex() * this.pageSize() >= rows.length) this.pageIndex.set(0); }
  setFilter(v: string) { this.filter.set(v); this.pageIndex.set(0); }
  sortBy(s: Sort) { this.sort.set(s); }
  pageBy(e: PageEvent) { this.pageIndex.set(e.pageIndex); this.pageSize.set(e.pageSize); }
}

function compare(a: unknown, b: unknown): number {
  if (a === b) return 0;
  if (a === null || a === undefined || a === '') return 1;
  if (b === null || b === undefined || b === '') return -1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
}

/** Builds a downloadable CSV from rows (client-side "table view" export). */
export function toCsv(columns: string[], rows: unknown[][]): string {
  const esc = (v: unknown) => {
    const s = v === null || v === undefined ? '' : String(v);
    return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  return [columns.map(esc).join(','), ...rows.map(r => r.map(esc).join(','))].join('\n');
}
