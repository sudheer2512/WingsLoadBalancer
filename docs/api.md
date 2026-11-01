# Administration API

Base URL: the admin host (for example `https://wings-admin.corp.local`). Interactive reference: `/api/docs` (Scalar).
Machine-readable OpenAPI 3 document: `/openapi/v1.json` (a copy is in [openapi.json](openapi.json)).

## Conventions

* **Authentication**: cookie session from `POST /api/auth/login` (administrators only; optional TOTP MFA).
* **CSRF**: every `POST/PUT/DELETE` must send `X-XSRF-TOKEN` with the value of the `XSRF-TOKEN` cookie (issued on any GET).
* **Authorisation**: each endpoint requires the permission listed below; built-in roles bundle them (`SuperAdministrator`, `RoutingAdministrator`, `Operator`, `ReadOnlyMonitor`, `Auditor`).
* **Audit reason**: send `X-Audit-Reason: <text>` to record why a change was made.
* **Errors**: `400 {error:'validation_failed', errors:[…]}`, `403`, `404`, `409 {error:'conflict'}` (optimistic concurrency: reload and retry), `422` (transfer checks failed).
* **Concurrency**: update requests accept `concurrencyStamp` / `expectedStamp` from the last read.
* Enums are strings (`Production`, `Dedicated`, `RoundRobin`, `PerSession`, …).
* The gateway's own endpoints (`/_wings/*`: login, logout, session, health, handoff) are documented in [architecture.md](architecture.md); they are not part of this API.

## Examples

```http
POST /api/routing/simulate
Content-Type: application/json
X-XSRF-TOKEN: …

{ "company": "ABC Motors", "username": "user1", "application": "ERP", "environment": "Production" }
```

```http
PUT /api/users/{userId}/assignment
X-Audit-Reason: Finance team moved to dedicated hardware

{ "companyId": "…", "primaryServerId": "…", "secondaryServerId": null, "failoverPolicyId": "…",
  "environment": "Production", "effectiveFrom": "2026-11-01T22:00:00Z", "expectedStamp": "…" }
```

```http
POST /api/servers/{id}/drain

{ "timeoutMinutes": 45, "reason": "Patch Tuesday" }
```

## Endpoints

### Administrators

| Method | Path | Permission |
|---|---|---|
| GET | `/api/admins` | admins.manage |
| POST | `/api/admins` | admins.manage |
| GET | `/api/admins/permissions` | admins.manage |
| GET | `/api/admins/roles` | admins.manage |
| PUT | `/api/admins/{id}` | admins.manage |

### Alerts

| Method | Path | Permission |
|---|---|---|
| GET | `/api/alerts` | alerts.read |
| GET | `/api/alerts/channels` | alerts.read |
| POST | `/api/alerts/channels` | alerts.manage |
| DELETE | `/api/alerts/channels/{id}` | alerts.manage |
| PUT | `/api/alerts/channels/{id}` | alerts.manage |
| POST | `/api/alerts/channels/{id}/test` | alerts.manage |
| POST | `/api/alerts/{id}/acknowledge` | alerts.manage |
| POST | `/api/alerts/{id}/resolve` | alerts.manage |

### Applications

| Method | Path | Permission |
|---|---|---|
| GET | `/api/applications` | servers.read |
| POST | `/api/applications` | policies.write |
| PUT | `/api/applications/{id}` | policies.write |
| POST | `/api/applications/{id}/environments` | policies.write |
| DELETE | `/api/applications/{id}/environments/{bindingId}` | policies.write |
| PUT | `/api/applications/{id}/environments/{bindingId}` | policies.write |

### Audit

| Method | Path | Permission |
|---|---|---|
| GET | `/api/audit` | audit.read |
| GET | `/api/audit/event-types` | audit.read |

### Auth

| Method | Path | Permission |
|---|---|---|
| GET | `/api/auth/csrf` | anonymous |
| POST | `/api/auth/login` | anonymous |
| POST | `/api/auth/logout` | signed-in |
| GET | `/api/auth/me` | anonymous |
| POST | `/api/auth/mfa/enable` | signed-in |
| POST | `/api/auth/mfa/setup` | signed-in |
| POST | `/api/auth/password` | signed-in |

### Companies

| Method | Path | Permission |
|---|---|---|
| GET | `/api/companies` | companies.read |
| POST | `/api/companies` | companies.write |
| GET | `/api/companies/{id}` | companies.read |
| PUT | `/api/companies/{id}` | companies.write |
| POST | `/api/companies/{id}/assignment` | assignments.write |
| POST | `/api/companies/{id}/assignment/preview` | assignments.read |
| POST | `/api/companies/{id}/assignments/{assignmentId}/end` | assignments.write |
| POST | `/api/companies/{id}/authorizations` | assignments.write |
| DELETE | `/api/companies/{id}/authorizations/{serverId}` | assignments.write |
| GET | `/api/companies/{id}/history` | audit.read |
| GET | `/api/companies/{id}/sessions` | sessions.read |
| POST | `/api/companies/{id}/shared-pool` | assignments.write |
| POST | `/api/companies/{id}/status` | companies.write |
| POST | `/api/companies/{id}/transfer` | assignments.write |
| GET | `/api/companies/{id}/users` | companies.read |

### Dashboard

| Method | Path | Permission |
|---|---|---|
| GET | `/api/dashboard/summary` | monitoring.read |
| GET | `/api/dashboard/timeseries` | monitoring.read |
| GET | `/api/dashboard/topology` | monitoring.read |
| GET | `/api/dashboard/utilization` | monitoring.read |

### Maintenance

| Method | Path | Permission |
|---|---|---|
| GET | `/api/maintenance` | servers.read |
| POST | `/api/maintenance` | servers.operate |
| POST | `/api/maintenance/{id}/cancel` | servers.operate |

### Policies

| Method | Path | Permission |
|---|---|---|
| GET | `/api/failover-policies` | pools.read |
| POST | `/api/failover-policies` | policies.write |
| PUT | `/api/failover-policies/{id}` | policies.write |
| GET | `/api/routing-policies` | pools.read |
| POST | `/api/routing-policies` | policies.write |
| PUT | `/api/routing-policies/{id}` | policies.write |

### Pools

| Method | Path | Permission |
|---|---|---|
| GET | `/api/pools` | pools.read |
| POST | `/api/pools` | pools.write |
| DELETE | `/api/pools/{id}` | pools.write |
| PUT | `/api/pools/{id}` | pools.write |
| PUT | `/api/pools/{id}/members` | pools.write |
| DELETE | `/api/pools/{id}/members/{serverId}` | pools.write |

### Reports

| Method | Path | Permission |
|---|---|---|
| GET | `/api/reports` | reports.read |
| GET | `/api/reports/{name}` | reports.read |

### Routing

| Method | Path | Permission |
|---|---|---|
| GET | `/api/routing/config` | monitoring.read |
| POST | `/api/routing/e2e` | routing.simulate |
| GET | `/api/routing/history` | monitoring.read |
| POST | `/api/routing/simulate` | routing.simulate |

### Servers

| Method | Path | Permission |
|---|---|---|
| GET | `/api/servers` | servers.read |
| POST | `/api/servers` | servers.write |
| DELETE | `/api/servers/{id}` | servers.write |
| GET | `/api/servers/{id}` | servers.read |
| PUT | `/api/servers/{id}` | servers.write |
| GET | `/api/servers/{id}/assignments` | assignments.read |
| GET | `/api/servers/{id}/backend-config` | secrets.export |
| POST | `/api/servers/{id}/disable` | servers.operate |
| POST | `/api/servers/{id}/drain` | servers.operate |
| POST | `/api/servers/{id}/enable` | servers.operate |
| GET | `/api/servers/{id}/health` | servers.read |
| GET | `/api/servers/{id}/history` | audit.read |
| POST | `/api/servers/{id}/maintenance` | servers.operate |
| GET | `/api/servers/{id}/metrics` | servers.read |
| PUT | `/api/servers/{id}/reservations` | servers.write |
| POST | `/api/servers/{id}/restore` | servers.operate |
| POST | `/api/servers/{id}/retire` | servers.write |
| GET | `/api/servers/{id}/sessions` | sessions.read |
| POST | `/api/servers/{id}/test` | servers.operate |

### Sessions

| Method | Path | Permission |
|---|---|---|
| GET | `/api/sessions` | sessions.read |
| POST | `/api/sessions/{sessionKey}/revoke` | sessions.revoke |

### Settings

| Method | Path | Permission |
|---|---|---|
| GET | `/api/settings` | monitoring.read |
| PUT | `/api/settings` | settings.write |

### Users

| Method | Path | Permission |
|---|---|---|
| GET | `/api/users` | companies.read |
| POST | `/api/users` | companies.write |
| POST | `/api/users/bulk-import` | assignments.write |
| GET | `/api/users/bulk-import/template` | assignments.read |
| DELETE | `/api/users/{id}/assignment` | assignments.write |
| GET | `/api/users/{id}/assignment` | assignments.read |
| PUT | `/api/users/{id}/assignment` | assignments.write |
| GET | `/api/users/{id}/assignment/history` | assignments.read |
| POST | `/api/users/{id}/assignment/preview` | assignments.read |
| POST | `/api/users/{id}/assignment/rollback` | assignments.write |
| PUT | `/api/users/{id}/password` | companies.write |
| PUT | `/api/users/{id}/status` | companies.write |

## Real-time hub

`/hubs/monitoring` (SignalR, requires `monitoring.read`). Server-to-client messages:
`summary` (dashboard summary, every ~6 s), `decisions` (new routing decisions), `health` (server health changes).
