# Wings Load Balancer — admin web UI

Angular 22 single-page app (standalone components, OnPush + signals, Angular Material 3, `@microsoft/signalr`) for the
admin API in `../src/Wings.Admin.Api`.

## Develop

```bash
npm install
../scripts/run-dev.sh          # admin API on :5290 (plus gateway and mock backends)
npm start                      # http://localhost:4200, proxies /api, /openapi and /hubs (WebSocket) to :5290
```

Sign in as `admin` with `Seed__AdminPassword` from `../App_Data/dev-credentials.env`.

## Build

```bash
npm run build                  # production build into ../src/Wings.Admin.Api/wwwroot (git-ignored)
```

The admin API serves `wwwroot` with a SPA fallback, so after a build the UI is at http://localhost:5290.
The API's Content-Security-Policy allows only `script-src 'self'`; the production configuration therefore disables
critical-CSS inlining (it would add an inline `onload` handler). Icons come from the `material-icons` npm package — no CDN.

## How it talks to the API

* Cookie session. `GET /api/auth/csrf` then `POST /api/auth/login`; `/api/auth/me` is read at start-up and after
  sign-in. Angular's XSRF support echoes the `XSRF-TOKEN` cookie as `X-XSRF-TOKEN` on every unsafe request.
* 401 → sign-in page (with `returnUrl`); 403 → "You don't have permission" message. Navigation, routes and buttons are
  filtered by the permission codes returned by `/me`.
* Optional audit reasons are sent as `X-Audit-Reason`. Assignment changes send `expectedStamp`; a 409 is shown as
  "changed by someone else — reload".
* Live data: SignalR hub `/hubs/monitoring` (`summary`, `decisions`, `health`).

## Structure

```
src/app/
  core/        api.service.ts (every endpoint), auth.service.ts (session, guards, interceptor, error helpers),
               monitoring.service.ts (SignalR), ui.service.ts (toasts, dialogs, cached lookups), models.ts
  shared/      charts.ts (SVG time/bar/sparkline charts), display.ts (status chip, meter, pipes, Grid helper),
               dialogs.ts, tabs.ts, filters.ts, audit-table.ts, preview-table.ts
  shell.ts     layout, login (+ MFA step), change password, MFA setup
  dashboard/   KPIs, traffic charts, utilisation grid, live decision feed, topology tree
  servers/     list, detail (overview/edit, assignments, sessions, performance, health, audit), dialogs
  companies/   list, detail (assignments, authorisations, users, sessions, history), assign/transfer dialogs
  users/       user assignment search + per-user panel, bulk upload
  pools/ policies/ routing/ (simulator, history, configuration) ops/ (maintenance, sessions, alerts, audit,
  reports, settings, administrators)
```

Charts are small hand-written SVG components with a fixed categorical palette (`--series-1…6` in `styles.scss`) that has
separate light and dark steps; status colours are reserved for state and always paired with an icon and label.
