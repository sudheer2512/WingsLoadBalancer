# Database model

SQL Server in production (`database/sqlserver/001_schema.sql`, generated from EF Core migrations), SQLite for local
development and tests. Same model, two migration sets (`src/Wings.LoadBalancer.Data/Migrations/{SqlServer,Sqlite}`).

Conventions:

* GUID primary keys (`Guid.CreateVersion7()`: time-ordered, so SQL Server clustered indexes don't fragment).
* Enums stored as readable strings (`nvarchar(40)`).
* `ConcurrencyStamp` (uniqueidentifier) on every administratively edited table is an EF concurrency token. A stale
  update fails with `409 Conflict` in the API.
* Assignments are **effective-dated** and never updated in place. A change closes the current row (`EffectiveTo`,
  `SupersededById`) and inserts a new one. That gives history, scheduling (future `EffectiveFrom`) and rollback.
* Every routing-relevant write inserts a `ConfigurationVersions` row in the same transaction (EF `SaveChanges`
  interceptor). Gateways reload when the newest version changes.
* Every change to routing configuration or administrative tables also writes an `AuditLogs` row in the same
  transaction, with previous and new values. Password hashes and MFA secrets are masked.

## Tables

| # | Table | Purpose | Key constraints / indexes |
|---|---|---|---|
| 1 | `Companies` | Tenants: code, name, plan, routing mode, status, auth mode, max concurrent users | unique `NormalizedCode` |
| 2 | `CompanyAliases` | Alternative names accepted at sign-in | unique `NormalizedAlias` (global) |
| 3 | `Users` | People: display name, password hash (PBKDF2), MFA, lockout | |
| 4 | `UserCompanyMemberships` | Login identity **within** a company (username, status, external subject) | unique `(CompanyId, NormalizedUsername)`, unique `(CompanyId, UserId)`, unique `(CompanyId, ExternalSubject)` filtered |
| 5 | `Applications` | Wings applications (ERP, HR, …), minimum server version | unique `Code` |
| 6 | `ApplicationEnvironments` | Public host + path prefix → application + environment, routing mode, timeout, body limit | unique `(PublicHost, PathPrefix)` |
| 7 | `Servers` | Server registry (all fields in spec §7, plus mTLS thumbprints, drain timeout, manual-restore flag) | unique `Name`; `(Environment, AdminState)` |
| 8 | `ServerPools` | Logical pools: algorithm, affinity, failover policy, eligible plans, capacity | unique `Name` |
| 9 | `ServerPoolMembers` | Pool membership: weight override, priority, accepting new sessions | unique `(PoolId, ServerId)`; `ServerId` |
| 10 | `CompanyServerAssignments` | Company → primary/secondary server or cluster pool, failover policy | unique `(CompanyId, Environment, ApplicationId, EffectiveFrom)`; `(CompanyId, IsEnabled, EffectiveTo)`; `PrimaryServerId` |
| 11 | `UserServerAssignments` | User (in a company) → dedicated server, secondary, failover policy | unique `(CompanyId, UserId, Environment, ApplicationId, EffectiveFrom)`; `PrimaryServerId` |
| 12 | `SharedPoolAssignments` | Company → permitted shared pool(s) with priority | unique `(CompanyId, PoolId, Environment, ApplicationId, EffectiveFrom)` |
| 13 | `FailoverPolicies` | Enabled flag, failover pool, failback policy, max attempts, priority | unique `Name` |
| 14 | `ServerHealthHistory` | Every health transition (from, to, reason, source) | `(ServerId, At)` |
| 15 | `ServerMetrics` | Sampled CPU, memory, disk, connections, requests, queue, errors, response time, sessions | `(ServerId, CapturedAt)`, `CapturedAt` |
| 16 | `RoutingPolicies` | Degraded-server policy, default affinity, **load-score metric weights**, session timeouts | unique `Name` |
| 17 | `RoutingDecisionLogs` | Logged decisions (new session, reroute, failover, failback, refusals) | `At`; `(CompanyId, At)`; `(ServerId, At)`; `(Outcome, At)` |
| 18 | `ActiveSessions` | One row per (session, application): server, gateway, last seen, revocation | unique `(SessionKey, ApplicationId)`; `(ServerId, EndedAt)`; `(CompanyId, EndedAt)`; `RevokedAt` |
| 19 | `MaintenanceWindows` | Scheduled maintenance with drain timeout and auto-restore | `(ServerId, StartsAt)`; `(Status, StartsAt)` |
| 20 | `AuditLogs` | Event id, UTC time, actor, company, user, server, previous/new values, reason, correlation id, source IP | `At`; `(EventType, At)`; `(CompanyId, At)`; `(EntityType, EntityId)` |
| 21 | `Administrators` | Admin accounts (PBKDF2, MFA, lockout) | unique `NormalizedUsername` |
| 22 | `Roles` | Built-in and custom roles | unique `Name` |
| 23 | `Permissions` | Permission catalog (`servers.read`, `assignments.write`, …) | unique `Code` |
| 24 | `SystemSettings` | Retention periods, alert thresholds, session idle time | unique `Key` |

Supporting tables:

| Table | Purpose |
|---|---|
| `AdministratorRoles`, `RolePermissions` | Many-to-many joins (unique pairs) |
| `ServerApplications`, `ServerPoolApplications` | Application compatibility of servers and pools |
| `CompanyServerAuthorizations` | Explicit "server may hold this company's data" grants. User-level assignments must stay inside the authorised set. |
| `CapacityReservations` | Sessions reserved on a server for an important company |
| `ServerHealthCurrent` | Current health and latest metrics per server (written by the monitor leader, read by gateways every few seconds) |
| `ConfigurationVersions` | Append-only routing configuration version log |
| `GatewayInstances`, `GatewayTrafficSamples` | Gateway heartbeats and traffic totals (dashboard, reports, gateway alerts) |
| `Alerts`, `NotificationChannels` | Alert lifecycle (deduplicated by `DedupKey`) and delivery channels |
| `ServiceLeases` | Leader election for the health monitor and operations services |
| `DataProtectionKeys` | Shared ASP.NET Core Data Protection key ring (session cookies valid on every gateway) |

## Relationships (simplified)

```
Companies 1─* CompanyAliases
Companies 1─* UserCompanyMemberships *─1 Users
Companies 1─* CompanyServerAssignments *─1 Servers (primary, secondary)   ─┐
Companies 1─* UserServerAssignments    *─1 Servers (primary, secondary)    ├─ FailoverPolicies ─ ServerPools (failover pool)
Companies 1─* SharedPoolAssignments    *─1 ServerPools                     ─┘
ServerPools 1─* ServerPoolMembers *─1 Servers
Applications 1─* ApplicationEnvironments ; Servers *─* Applications ; ServerPools *─* Applications
Servers 1─1 ServerHealthCurrent ; Servers 1─* ServerHealthHistory / ServerMetrics / MaintenanceWindows
Administrators *─* Roles *─* Permissions
```

## Scripts

| File | Content |
|---|---|
| `database/sqlserver/001_schema.sql` | Idempotent schema (all migrations; checks `__EFMigrationsHistory`) |
| `database/sqlserver/002_reference_data.sql` | Permissions, built-in roles, default settings, default routing policy (idempotent `MERGE`) |
| `database/sqlserver/003_sample_data.sql` | Sample companies ABC/XYZ/GFL, users, 8 servers, 3 pools, assignments (test environments only) |
| `database/sqlite/001_schema.sql` | SQLite schema (reference; the apps migrate SQLite automatically in development) |

Regenerate after a model change:

```bash
dotnet ef migrations add <Name> --project src/Wings.LoadBalancer.Data --startup-project src/Wings.LoadBalancer.Data --context SqlServerWingsDbContext --output-dir Migrations/SqlServer
```

```bash
dotnet ef migrations add <Name> --project src/Wings.LoadBalancer.Data --startup-project src/Wings.LoadBalancer.Data --context SqliteWingsDbContext --output-dir Migrations/Sqlite
```

```bash
dotnet ef migrations script --idempotent --project src/Wings.LoadBalancer.Data --startup-project src/Wings.LoadBalancer.Data --context SqlServerWingsDbContext -o database/sqlserver/001_schema.sql
```

## Volume guidance

`RoutingDecisionLogs` only records decision *changes* (new session, reroute, failover, refusal), not every request,
so it grows with sessions rather than traffic. Request volume is in `GatewayTrafficSamples` (one row per gateway every
15 s). `ServerMetrics` stores one sample per server per minute by default. Retention for all of them is in
`SystemSettings` and is applied by the operations service every 6 hours.
