# Operations runbook

Admin UI paths are given as *Menu → Page*. Every action below is also available through the REST API
(see [api.md](api.md)) and is written to the audit log.

## Daily checks

1. *Dashboard*: every gateway **online** and **in sync** (configuration version matches *Routing → Configuration*).
2. *Alerts*: no open Critical alerts. Acknowledge what you are working on so others know.
3. *Routing → Configuration*: no validation **errors** (isolation conflicts, environment mismatches, missing servers).

## Planned maintenance of an application server

1. *Servers → (server) → Drain*. Enter a timeout and a reason.
   * New sessions stop immediately. Existing sessions keep their affinity.
   * Dedicated tenants whose policy permits failover go to their approved alternatives for **new** sessions; tenants
     without failover get a controlled 503 for new sessions. Check *Routing Simulator* first if you are unsure.
2. Watch *Active sessions* on the server page. The server moves to **Maintenance** when sessions reach zero or the
   drain timeout expires (audit `ServerDrainCompleted`). Sessions still on the server then fail over per policy.
3. Do the work.
4. *Restore*. The server takes traffic again only while health checks pass. Alternatively schedule it:
   *Maintenance → Schedule window* (drain at start; auto-restore after the end once health is stable).

## Unplanned server failure

Symptoms: alert `ServerUnavailable`, dashboard Offline, `NoHealthyDestination` alerts for its tenants.

1. The routing engine already applies failover policies. Confirm in *Routing history* (filter by server; event
   `Failover`) and in the audit log (`AutomaticFailover`).
2. Tenants **without** failover receive 503 for that server's traffic. Options:
   * restore the server, or
   * attach a failover policy (*Companies → Assignments → change*, keep the same primary, choose a policy), or
   * transfer the company (*Companies → Transfer*: run **Validate** first).
3. After recovery the server passes through **Recovering** (stabilisation period) before it is Healthy. Servers with
   *Require manual restore* stay out of rotation until you press **Restore**.

## Moving a company to another server

1. *Companies → (company) → Transfer → Validate*. All checks must pass: environment, application, isolation, target
   health, accepting sessions, version, capacity.
2. Choose a cutover:
   * **Immediate**: new assignment now. Existing sessions follow on their next request (needs stateless application
     servers or shared session state).
   * **Scheduled**: pick an off-peak time. The change is stored now and takes effect then.
   * **Force re-authentication**: immediate, and every session of the company is revoked.
3. Confirm with *Routing Simulator* and *Routing history*.

## Assigning users

* *User assignments → search → Assign dedicated server*. Only servers authorised for the company's data can be
  selected. To add one: *Companies → Data authorisations → Grant* (a dedicated server owned by another tenant is refused).
* **Preview** shows the before/after routing decision without saving.
* Set **Effective from** in the future to schedule. Use **History → Rollback** to restore an earlier assignment.
* Bulk changes: *User assignments → Bulk upload*. Always run the dry run, and commit only when every row is valid.
  Template: `deploy/config/sample-user-assignments.csv`.

## Gateway incidents

| Symptom | Check | Action |
|---|---|---|
| NLB marks a gateway down | `/_wings/health/ready` body: `starting`, `draining`, `stale-configuration` | `stale-configuration`: the gateway cannot reach SQL for longer than `Routing:LastKnownGoodMaxAgeMinutes`. Restore SQL connectivity. The other gateways keep serving. |
| Users signed out when switching gateway | Data Protection key ring | All instances must share the key ring (`DataProtectionKeys` table, file share, or Redis) and use the same application name. |
| Gateway `ConfigurationStale` alert | Gateway logs | SQL unreachable from that host. Routing continues on last-known-good until the limit, then fails closed (by design). |
| Decision latency rising on the dashboard | `wings.routing.decision.duration` metric | Normally a few µs. Check CPU starvation on the gateway host. |

## Dependency outages

| Component down | Behaviour | Operator action |
|---|---|---|
| **SQL Server** | Gateways route with the last-known-good snapshot (in memory, and the protected `App_Data/routing-lkg.bin` after a restart) for `Routing:LastKnownGoodMaxAgeMinutes` (default 30). Existing sessions continue; local-password sign-ins return a controlled 503; audit/decision logs are buffered (bounded) and dropped beyond the buffer. After the limit, gateways fail closed. | Restore SQL. Raise the limit only with a security sign-off; it bounds how long revoked access or changed assignments can be missed. |
| **Redis** | Sessions, affinity and invalidation fall back to in-memory per gateway. Revocations still propagate (2 s DB poll), configuration changes within `ConfigurationSync:VersionPollSeconds`. Hash-based affinity (per user/company) stays identical on every gateway; per-session affinity may be re-resolved when a user lands on a different gateway. | Restore Redis. No restart needed (connections auto-recover). |
| **Monitor service** | Gateways keep the last health they saw. After `Health:SharedStateMaxAgeSeconds` (120 s) unknown health means **not routable**, plus passive checks still quarantine failing servers. | Start the standby monitor (it takes over automatically if running). As an emergency measure set `Health:GatewayMode=Local` on gateways so each probes the servers itself. |
| **Wings auth service** | Sign-ins for companies in `WingsAuthService` mode return 503; existing sessions continue. | Restore the service. |

## Revoking access

* One session: *Sessions → Revoke*.
* A user: *User assignments → Disable membership*. Every gateway blocks the user on the next request after the
  configuration reload (≤ poll interval) and signs them out.
* A company: *Companies → Disable access* (optionally revoke all sessions).

## Alerts reference

| Alert | Raised when | Typical action |
|---|---|---|
| ServerUnavailable (Critical) | Health Unhealthy | See *Unplanned server failure* |
| HighCpu / HighMemory / HighErrorRate / SlowResponse | Above thresholds in *Settings* | Investigate the application server; consider draining |
| NearCapacity | Sessions ≥ `Alerts.CapacityPercent` of MaxCapacity | Add pool capacity or transfer tenants |
| NoHealthyDestination (Critical) | Requests refused for a tenant in the last 5 min | Restore the server or adjust failover policy |
| UnexpectedFailover | Failovers for a tenant whose primary is not draining | Check the primary's health history |
| GatewayConnectivity | Gateway heartbeat stale or gateway reports SQL unavailable | Check that gateway host |
| RedisUnavailable | Redis configured but unreachable | Restore Redis |
| ConfigurationInconsistency | Validator errors, or a gateway on stale configuration | Fix the configuration listed in the alert |
| SqlUnavailable | Logged at Critical level (cannot be stored while SQL is down) | Alert on it from your log platform |

Alerts are deduplicated per condition and auto-resolve when the condition clears. Channels: *Alerts → Channels*
(e-mail, webhook with HMAC signature `X-Wings-Signature: sha256=…`, Microsoft Teams).

## Backups and retention

* Back up the SQL database (full + log). It holds configuration, assignments, audit and the Data Protection key ring.
* Retention is in *Settings* (`Retention.*`), applied every 6 hours by the operations service.
* Rotating `BackendTrust:MasterKey` changes every backend's derived keys. Roll it out to gateways and backends together
  during a maintenance window.

## Useful local commands

```bash
./scripts/run-dev.sh
```

```bash
./scripts/stop-dev.sh
```

```bash
dotnet test Wings.LoadBalancer.slnx
```

```bash
dotnet run -c Release --project tests/Wings.Benchmarks
```
