import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed, inject, input, signal,
} from '@angular/core';

/** One series; `slot` (1–6) picks the fixed categorical colour so colour follows the entity, not its rank. */
export interface ChartSeries { name: string; values: (number | null | undefined)[]; slot?: number; }

const M = { top: 10, right: 14, bottom: 26, left: 52 };

/** "Nice" axis ticks covering [min, max]. */
export function niceTicks(min: number, max: number, count = 4): number[] {
  if (!isFinite(min) || !isFinite(max)) return [0, 1];
  if (max === min) max = min + 1;
  const raw = (max - min) / count;
  const mag = Math.pow(10, Math.floor(Math.log10(raw)));
  const step = [1, 2, 2.5, 5, 10].map(s => s * mag).find(s => s >= raw) ?? raw;
  const start = Math.floor(min / step) * step;
  const ticks: number[] = [];
  for (let v = start; v <= max + step * 0.5; v += step) ticks.push(+v.toFixed(10));
  return ticks;
}

export function fmtNum(v: number | null | undefined, unit = ''): string {
  if (v === null || v === undefined || isNaN(v)) return '—';
  const a = Math.abs(v);
  const s = Number.isInteger(v) && a < 1e4 ? String(v) : a >= 1e6 ? (v / 1e6).toFixed(1) + 'M' : a >= 1e4 ? (v / 1e3).toFixed(0) + 'k' : a >= 1000 ? (v / 1e3).toFixed(1) + 'k'
    : a >= 100 ? v.toFixed(0) : a >= 10 ? v.toFixed(1) : v.toFixed(2).replace(/\.?0+$/, '') || '0';
  return unit ? `${s} ${unit}`.replace(' %', '%') : s;
}

function fmtTime(t: number, spanMs: number): string {
  const d = new Date(t);
  const hm = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  if (spanMs <= 36 * 3600_000) return hm;
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + (spanMs <= 5 * 86400_000 ? ' ' + hm : '');
}

/** Observes the host width so charts are responsive without distorting text. */
function useWidth(initial = 600) {
  const el = inject(ElementRef<HTMLElement>);
  const width = signal(initial);
  const destroy = inject(DestroyRef);
  afterNextRender(() => {
    const host = el.nativeElement as HTMLElement;
    width.set(Math.max(200, host.clientWidth || initial));
    const ro = new ResizeObserver(entries => {
      const w = Math.round(entries[0]?.contentRect.width ?? initial);
      if (w > 0 && Math.abs(w - width()) > 2) width.set(Math.max(200, w));
    });
    ro.observe(host);
    destroy.onDestroy(() => ro.disconnect());
  });
  return width;
}

/**
 * Time-series chart (line, area or stacked bars) with labelled axes, legend and a crosshair tooltip.
 * One y-axis only; plot series of different units in separate charts.
 */
@Component({
  selector: 'app-time-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'time-chart' },
  template: `
    @if (series().length > 1) {
      <div class="legend" role="list">
        @for (s of series(); track s.name) {
          <span class="item" role="listitem"><span class="sw" [style.background]="color(s, $index)"></span>{{ s.name }}</span>
        }
      </div>
    }
    @if (times().length === 0) {
      <div class="nodata" [style.height.px]="height()">{{ emptyText() }}</div>
    } @else {
      <svg [attr.width]="width()" [attr.height]="height()" role="img" [attr.aria-label]="ariaLabel()">
        <g class="grid">
          @for (t of yTicks(); track t) {
            <line [attr.x1]="m.left" [attr.x2]="width() - m.right" [attr.y1]="y(t)" [attr.y2]="y(t)" />
            <text class="ylab" [attr.x]="m.left - 6" [attr.y]="y(t) + 4" text-anchor="end">{{ fmt(t) }}</text>
          }
          @for (t of xTicks(); track t) {
            <text class="xlab" [attr.x]="x(t)" [attr.y]="height() - 8" text-anchor="middle">{{ fmtT(t) }}</text>
          }
          <line class="axis" [attr.x1]="m.left" [attr.x2]="width() - m.right" [attr.y1]="y(yTicks()[0])" [attr.y2]="y(yTicks()[0])" />
        </g>
        @if (yLabel()) {
          <text class="ytitle" [attr.transform]="'translate(12,' + (m.top + plotH() / 2) + ') rotate(-90)'" text-anchor="middle">{{ yLabel() }}</text>
        }
        @if (kind() === 'bar') {
          @for (b of bars(); track $index) {
            <rect [attr.x]="b.x" [attr.y]="b.y" [attr.width]="b.w" [attr.height]="b.h" [attr.fill]="b.color" rx="1.5" />
          }
        } @else {
          @for (p of paths(); track p.name) {
            @if (kind() === 'area') { <path [attr.d]="p.area" [attr.fill]="p.color" fill-opacity="0.12" stroke="none" /> }
            <path [attr.d]="p.line" [attr.stroke]="p.color" fill="none" stroke-width="2" stroke-linejoin="round" stroke-linecap="round" />
            @for (d of p.isolated; track $index) { <circle [attr.cx]="d.x" [attr.cy]="d.y" r="3" [attr.fill]="p.color" /> }
          }
        }
        @if (hover(); as h) {
          <line class="cross" [attr.x1]="h.x" [attr.x2]="h.x" [attr.y1]="m.top" [attr.y2]="m.top + plotH()" />
          @if (kind() !== 'bar') {
            @for (d of h.dots; track $index) { <circle [attr.cx]="h.x" [attr.cy]="d.y" r="4" [attr.fill]="d.color" class="dot" /> }
          }
        }
        <rect class="hit" [attr.x]="m.left" [attr.y]="m.top" [attr.width]="plotW()" [attr.height]="plotH()"
              (mousemove)="move($event)" (mouseleave)="hoverIndex.set(-1)" />
      </svg>
      @if (hover(); as h) {
        <div class="tip" [style.left.px]="h.tipX" [style.top.px]="8">
          <div class="tt">{{ h.time }}</div>
          @for (r of h.rows; track r.name) {
            <div class="tr"><span class="sw" [style.background]="r.color"></span><span class="nm">{{ r.name }}</span><b>{{ r.value }}</b></div>
          }
        </div>
      }
      @if (tableView()) {
        <details class="tv"><summary>View as table</summary>
          <div class="tv-wrap"><table class="data"><thead><tr><th>Time</th>@for (s of series(); track s.name) { <th class="num">{{ s.name }}{{ unit() ? ' (' + unit() + ')' : '' }}</th> }</tr></thead>
            <tbody>@for (t of times(); track $index; let i = $index) {
              <tr><td>{{ fmtFull(t) }}</td>@for (s of series(); track s.name) { <td class="num">{{ fmtCell(s.values[i]) }}</td> }</tr>
            }</tbody></table></div>
        </details>
      }
    }`,
  styles: `
    :host { display: block; position: relative; min-width: 0; }
    .tv { margin-top: 4px; font-size: 12px; } .tv summary { cursor: pointer; color: var(--muted); }
    .tv-wrap { max-height: 220px; overflow: auto; margin-top: 4px; }
    svg { display: block; overflow: visible; }
    .legend { display: flex; flex-wrap: wrap; gap: 4px 14px; font-size: 12px; color: var(--muted); margin-bottom: 4px; }
    .item { display: inline-flex; align-items: center; gap: 5px; }
    .sw { display: inline-block; width: 10px; height: 10px; border-radius: 2px; flex: none; }
    .grid line { stroke: var(--grid); stroke-width: 1; }
    .grid line.axis { stroke: var(--axis); }
    .ylab, .xlab, .ytitle { fill: var(--muted); font-size: 11px; }
    .cross { stroke: var(--axis); stroke-dasharray: 3 3; }
    .dot { stroke: var(--surface); stroke-width: 2; }
    .hit { fill: transparent; cursor: crosshair; }
    .nodata { display: grid; place-items: center; color: var(--muted); border: 1px dashed var(--border); border-radius: 6px; }
    .tip { position: absolute; pointer-events: none; background: var(--surface); border: 1px solid var(--border); border-radius: 6px;
      box-shadow: 0 4px 14px rgba(0,0,0,.15); padding: 6px 8px; font-size: 12px; min-width: 140px; z-index: 2; color: var(--text); }
    .tt { color: var(--muted); margin-bottom: 2px; }
    .tr { display: flex; align-items: center; gap: 6px; } .nm { flex: 1; } b { font-variant-numeric: tabular-nums; font-weight: 600; }
  `,
})
export class TimeChart {
  /** Epoch milliseconds, one per bucket. */
  times = input<number[]>([]);
  series = input<ChartSeries[]>([]);
  kind = input<'line' | 'area' | 'bar'>('line');
  unit = input('');
  yLabel = input('');
  yMax = input<number | null>(null);
  height = input(200);
  emptyText = input('No data for this period');
  ariaLabel = input('Time series chart');
  tableView = input(true);
  m = M;
  fmtFull = (t: number) => new Date(t).toLocaleString();
  fmtCell = (v: number | null | undefined) => fmtNum(v);

  width = useWidth();
  hoverIndex = signal(-1);
  plotW = computed(() => Math.max(10, this.width() - M.left - M.right));
  plotH = computed(() => Math.max(10, this.height() - M.top - M.bottom));

  private tRange = computed(() => {
    const t = this.times();
    if (t.length === 0) return [0, 1];
    if (t.length === 1) return [t[0] - 60_000, t[0] + 60_000];
    return [t[0], t[t.length - 1]];
  });
  private stackTotals = computed(() => this.times().map((_, i) => this.series().reduce((s, x) => s + (x.values[i] ?? 0), 0)));
  yTicks = computed(() => {
    const vals = this.kind() === 'bar' ? this.stackTotals() : this.series().flatMap(s => s.values.filter((v): v is number => v != null));
    const max = Math.max(this.yMax() ?? 0, ...vals, 0);
    return niceTicks(0, max > 0 ? max : 1, 4);
  });
  private barSlot = computed(() => {
    const n = Math.max(1, this.times().length);
    return this.plotW() / n;
  });

  x = (t: number) => {
    const [a, b] = this.tRange();
    if (this.kind() === 'bar') {
      const i = this.times().indexOf(t);
      if (i >= 0) return M.left + this.barSlot() * (i + 0.5);
    }
    return M.left + ((t - a) / (b - a || 1)) * this.plotW();
  };
  y = (v: number) => {
    const ticks = this.yTicks();
    const top = ticks[ticks.length - 1] || 1;
    return M.top + this.plotH() - (v / top) * this.plotH();
  };

  xTicks = computed(() => {
    const t = this.times();
    if (t.length === 0) return [];
    const want = Math.max(2, Math.min(7, Math.floor(this.plotW() / 110)));
    if (t.length <= want) return t;
    const step = (t.length - 1) / (want - 1);
    return Array.from({ length: want }, (_, i) => t[Math.round(i * step)]);
  });

  color(s: ChartSeries, i: number) { return `var(--series-${s.slot ?? (i % 6) + 1})`; }
  fmt = (v: number) => fmtNum(v, this.unit() === '%' ? '%' : '');
  fmtT = (t: number) => fmtTime(t, this.tRange()[1] - this.tRange()[0]);

  paths = computed(() => this.series().map((s, si) => {
    const pts: string[] = [];
    let line = '';
    let segment = false;
    s.values.forEach((v, i) => {
      if (v === null || v === undefined) { segment = false; return; }
      const px = this.x(this.times()[i]).toFixed(1), py = this.y(v).toFixed(1);
      line += `${segment ? 'L' : 'M'}${px},${py}`;
      segment = true;
      pts.push(`${px},${py}`);
    });
    const base = this.y(0).toFixed(1);
    const first = pts[0]?.split(',')[0], last = pts[pts.length - 1]?.split(',')[0];
    const area = pts.length ? `M${first},${base}L${pts.join('L')}L${last},${base}Z` : '';
    const isolated = s.values.map((v, i) => ({ v, i })).filter(({ v, i }) => v != null && s.values[i - 1] == null && s.values[i + 1] == null)
      .map(({ v, i }) => ({ x: this.x(this.times()[i]), y: this.y(v as number) }));
    return { name: s.name, color: this.color(s, si), line, area, isolated };
  }));

  bars = computed(() => {
    const slot = this.barSlot();
    const w = Math.max(1, Math.min(28, slot - 2));
    const out: { x: number; y: number; w: number; h: number; color: string }[] = [];
    this.times().forEach((_, i) => {
      let acc = 0;
      const cx = M.left + slot * (i + 0.5) - w / 2;
      this.series().forEach((s, si) => {
        const v = s.values[i] ?? 0;
        if (v <= 0) return;
        const y1 = this.y(acc), y2 = this.y(acc + v);
        // 2px surface gap between stacked segments
        out.push({ x: cx, y: y2, w, h: Math.max(1, y1 - y2 - (acc > 0 ? 2 : 0)), color: this.color(s, si) });
        acc += v;
      });
    });
    return out;
  });

  hover = computed(() => {
    const i = this.hoverIndex();
    const t = this.times();
    if (i < 0 || i >= t.length) return null;
    const x = this.x(t[i]);
    const unit = this.unit();
    const rows = this.series().map((s, si) => ({ name: s.name, color: this.color(s, si), value: fmtNum(s.values[i], unit) }));
    let acc = 0;
    const dots = this.series().map((s, si) => {
      const v = s.values[i];
      acc += v ?? 0;
      return v == null ? null : { y: this.y(this.kind() === 'bar' ? acc : v), color: this.color(s, si) };
    }).filter((d): d is { y: number; color: string } => d !== null);
    const tipX = x + 160 > this.width() ? Math.max(0, x - 170) : x + 12;
    return { x, rows, dots, tipX, time: new Date(t[i]).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) };
  });

  move(e: MouseEvent) {
    const t = this.times();
    if (!t.length) return;
    const rect = (e.currentTarget as SVGRectElement).getBoundingClientRect();
    const px = e.clientX - rect.left + M.left;
    let best = 0, bestD = Infinity;
    t.forEach((tt, i) => { const d = Math.abs(this.x(tt) - px); if (d < bestD) { bestD = d; best = i; } });
    this.hoverIndex.set(best);
  }
}

/** Horizontal bar chart for categorical comparisons (e.g. sessions per server), value labels at bar ends. */
@Component({
  selector: 'app-bar-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (items().length === 0) { <div class="nodata">{{ emptyText() }}</div> }
    @for (it of items(); track it.label) {
      <div class="bar-row" [class.hl]="hovered() === it.label" (mouseenter)="hovered.set(it.label)" (mouseleave)="hovered.set(null)"
           [attr.title]="it.label + ': ' + fmt(it.value)">
        <span class="lbl">{{ it.label }}</span>
        <span class="track"><span class="fill" [style.width.%]="pct(it.value)" [style.background]="'var(--series-' + slot() + ')'"></span></span>
        <span class="val">{{ fmt(it.value) }}</span>
      </div>
    }`,
  styles: `
    :host { display: block; }
    .bar-row { display: grid; grid-template-columns: minmax(80px, 140px) 1fr 64px; align-items: center; gap: 8px; padding: 3px 4px; border-radius: 4px; font-size: 12px; }
    .bar-row.hl { background: var(--hover); }
    .lbl { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .track { height: 12px; background: transparent; position: relative; }
    .fill { display: block; height: 100%; border-radius: 0 4px 4px 0; min-width: 2px; }
    .val { text-align: right; font-variant-numeric: tabular-nums; color: var(--text); }
    .nodata { color: var(--muted); padding: 16px; text-align: center; }
  `,
})
export class BarChart {
  items = input<{ label: string; value: number }[]>([]);
  unit = input('');
  slot = input(1);
  max = input<number | null>(null);
  emptyText = input('No data');
  hovered = signal<string | null>(null);
  private top = computed(() => this.max() ?? Math.max(1, ...this.items().map(i => i.value)));
  pct(v: number) { return Math.max(0, Math.min(100, (v / this.top()) * 100)); }
  fmt(v: number) { return fmtNum(v, this.unit()); }
}

/** Tiny trend line; the latest value is shown as text beside it by the caller. */
@Component({
  selector: 'app-sparkline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg [attr.width]="width()" [attr.height]="height()" role="img" [attr.aria-label]="label() + ' trend'">
      <title>{{ label() }}: min {{ stats().min }}, max {{ stats().max }}, latest {{ stats().last }}</title>
      @if (d()) {
        <path [attr.d]="d()" fill="none" [attr.stroke]="'var(--series-' + slot() + ')'" stroke-width="1.5" stroke-linejoin="round" />
        <circle [attr.cx]="lastPt().x" [attr.cy]="lastPt().y" r="2" [attr.fill]="'var(--series-' + slot() + ')'" />
      } @else {
        <line x1="0" [attr.x2]="width()" [attr.y1]="height() / 2" [attr.y2]="height() / 2" stroke="var(--grid)" stroke-dasharray="2 2" />
      }
    </svg>`,
  styles: `:host { display: inline-block; vertical-align: middle; line-height: 0; } svg { overflow: visible; }`,
})
export class Sparkline {
  values = input<(number | null | undefined)[]>([]);
  width = input(84);
  height = input(22);
  slot = input(1);
  label = input('');
  /** Fixed upper bound (e.g. 100 for percentages); otherwise scaled to the data. */
  max = input<number | null>(null);
  private clean = computed(() => this.values().map(v => (v == null || isNaN(v) ? null : v)));
  private scale = computed(() => {
    const nums = this.clean().filter((v): v is number => v !== null);
    const max = this.max() ?? Math.max(...nums, 0);
    const min = this.max() != null ? 0 : Math.min(...nums, 0);
    return { min, max: max === min ? min + 1 : max };
  });
  private pts = computed(() => {
    const v = this.clean();
    const { min, max } = this.scale();
    const n = v.length;
    return v.map((val, i) => val === null ? null : {
      x: n <= 1 ? this.width() : (i / (n - 1)) * this.width(),
      y: 1 + (this.height() - 2) * (1 - (val - min) / (max - min)),
    });
  });
  d = computed(() => {
    let s = '', pen = false;
    for (const p of this.pts()) {
      if (!p) { pen = false; continue; }
      s += `${pen ? 'L' : 'M'}${p.x.toFixed(1)},${p.y.toFixed(1)}`;
      pen = true;
    }
    return s.includes('L') ? s : '';
  });
  lastPt = computed(() => [...this.pts()].reverse().find(p => p) ?? { x: 0, y: 0 });
  stats = computed(() => {
    const nums = this.clean().filter((v): v is number => v !== null);
    return { min: fmtNum(nums.length ? Math.min(...nums) : null), max: fmtNum(nums.length ? Math.max(...nums) : null), last: fmtNum(nums[nums.length - 1]) };
  });
}
