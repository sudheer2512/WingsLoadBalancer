import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import { MatTabsModule } from '@angular/material/tabs';

export interface TabDef { key: string; label: string; }

/**
 * Tab header + panel. Content is projected (not portalled), so signal updates inside the active tab always render.
 * Usage: <app-tabs [tabs]="tabs" [(active)]="tab"> @switch (tab()) { @case ('a') { … } } </app-tabs>
 */
@Component({
  selector: 'app-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatTabsModule],
  template: `
    <nav mat-tab-nav-bar [tabPanel]="panel" [disableRipple]="true" class="tabs-nav">
      @for (t of tabs(); track t.key) {
        <a mat-tab-link [active]="active() === t.key" (click)="select(t.key)" (keydown.enter)="select(t.key)" (keydown.space)="$event.preventDefault(); select(t.key)">{{ t.label }}</a>
      }
    </nav>
    <mat-tab-nav-panel #panel class="tabs-panel"><ng-content /></mat-tab-nav-panel>`,
  styles: `.tabs-nav { margin-bottom: 12px; } .tabs-nav a { cursor: pointer; }`,
})
export class Tabs {
  tabs = input.required<TabDef[]>();
  active = model.required<string>();
  changed = output<string>();
  select(key: string) {
    if (this.active() === key) return;
    this.active.set(key);
    this.changed.emit(key);
  }
}
