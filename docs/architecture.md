# Wings Intelligent Load Balancer — Architecture

## 1. Purpose

Users open one public URL (for example `https://erp.example.com`). The gateway authenticates them, works out which
application server holds their company's data, and proxies every request to that server. Users never see or need
internal server addresses.

## 2. Components

```
                     ┌──────────────────────── External network load balancer ───────────────────────┐
                     │                                                                                │
             ┌───────▼───────┐                                                        ┌───────▼───────┐
             │  Gateway-01   │   identical, stateless (shared state in Redis/SQL)      │  Gateway-02   │
             │ Wings.Gateway │                                                        │ Wings.Gateway │
             └──┬─────┬──────┘                                                        └──┬─────┬──────┘
                │     │ YARP IHttpForwarder (HTTP/1.1, HTTP/2, WebSockets, streaming)      │     │
                │     └───────────────┬─────────────────────┬────────────────────────────┘     │
                │              ┌──────▼──────┐       ┌──────▼──────┐      ┌─────────────┐      │
                │              │ APP-SERVER-01│ ...  │ APP-SERVER-05│      │ Wings auth  │◄─────┤ (optional)
                │              └─────────────┘       └─────────────┘      │  service /  │
                │                     ▲ GET /health/ready (protected)      │  OIDC IdP   │
                │                     │                                    └─────────────┘
       ┌────────▼────────┐    ┌───────┴────────────┐     ┌──────────────────────────┐
       │ Redis           │◄───┤ Wings.Monitor      │     │ Wings.Admin.Api          │
       │ config snapshot │    │ (Windows Service / │     │ REST + SignalR + Angular │
       │ invalidation    │    │  container)        │     │ admin UI                 │
       │ sessions, DP key│    │ health, metrics,   │     └────────────┬─────────────┘
       └────────▲────────┘    │ drain, alerts      │                  │
                │             └───────┬────────────┘                  │
                └─────────────────────┴──────────── SQL Server ◄──────┘
                                                   (configuration, assignments, audit, history)
```

| Project | Role |
|---|---|
| `Wings.Routing.Core` | Routing policy engine. Pure C#, no YARP/EF/ASP.NET dependency. Immutable `RoutingSnapshot`, `RoutingEngine`, load-balancing strategies, health state machine, load scoring. |
| `Wings.LoadBalancer.Data` | EF Core model (SQL Server + SQLite), migrations, `DbRoutingConfigurationSource` (database → snapshot), audit/version interceptor. |
| `Wings.LoadBalancer.Infrastructure` | Redis/in-memory caching, config synchronisation, active/passive health monitoring, audit, alerts and notification channels, password/TOTP, authentication providers, reports and exports, SSRF guard. |
| `Wings.Gateway` | Public entry point. Login, session, routing, YARP forwarding, header hygiene, passive health, decision logging, readiness/liveness. |
| `Wings.Admin.Api` | Administrative REST API (OpenAPI), SignalR monitoring hub, hosts the Angular build. Not exposed publicly. |
| `Wings.Monitor.Service` | Optional standalone host (Windows Service or container) for the health monitor, drain controller, maintenance scheduler and alert evaluator. The same services can run inside `Wings.Admin.Api`. |
| `samples/Wings.MockBackend` | Sample backend application server that identifies itself, exposes `/health/ready`, WebSockets, uploads/downloads, and verifies gateway-signed identity headers. |
| `web/` | Angular admin application. |

## 3. Routing engine

The engine is a pure function of:

* an immutable, versioned `RoutingSnapshot` (configuration),
* an `IServerStateView` (runtime health/load: active + passive checks),
* a `RoutingRequest` built **only from trusted server-side context**: the authenticated session (CompanyId, UserId),
  the application and environment resolved from the request host/path binding, and the session's affinity record.

### 3.1 Resolution order (deterministic)

1. **Tenant validation** — company exists and is active; user is not disabled/locked; the application is published
   in the requested environment. Failure → `Denied`.
2. **User assignment** — enabled, inside its effective window, same environment; application-specific beats
   application-wide; later `EffectiveFrom` wins.
3. **Company assignment** — same rules. Optionally a *cluster pool* of permitted servers with a designated primary
   (`PrimaryPreferred` or `Balanced`).
4. **Shared pool assignment** — the company must be explicitly assigned to the pool and meet the pool's tenant
   eligibility (plan). No assignment anywhere → `NoAssignment` (fail closed; never "any available server").
5. **Selection** — eligible healthy member chosen by the pool's algorithm, after applying session affinity.
6. **Failover** — only to servers named by the applicable failover policy (secondary server, failover pool, cluster
   members). Disabled → `NoHealthyDestination`, which the gateway returns as a controlled `503`.

### 3.2 Eligibility (applied to *every* candidate, including affinity and failover)

* Environment matches the request.
* Server hosts the application (`ServerApplications`).
* Admin state `Active` (or `Draining` for **existing** sessions only). `Maintenance`, `Disabled`, `Retired` never.
* Health `Healthy`, or `Degraded` when the routing policy allows it.
* **Isolation**: a `Dedicated` server only serves the companies that own it through assignments.
* **Data authorisation**: a user-level assignment may only point to a server authorised for the company's data
  (explicit `CompanyServerAuthorizations` grants plus servers named by the company's own assignments and pools).
* Capacity: new sessions are refused when active sessions reach capacity, minus capacity reserved for other companies.
* Pool member not removed from new allocation (new sessions only).

### 3.3 Load-balancing strategies

Round robin, smooth weighted round robin, least active requests, weighted least requests, lowest utilisation (load
score), consistent hashing on CompanyId, consistent hashing on CompanyId+UserId. Hashing uses weighted rendezvous
(HRW) hashing, so every gateway instance makes the same choice without coordination, and only keys on a removed server
move.

**Load score** (lower is better) = Σ weightᵢ × normalisedᵢ over CPU %, memory %, active requests / capacity, response
time / threshold, error rate. Weights are configured in `RoutingPolicies`. The score is only used to rank servers the
tenant is already authorised to use.

### 3.4 Session affinity

`None`, `PerUser`, `PerCompany` (hash-based, consistent across gateways) or `PerSession` (server-side affinity record in
the session store). Affinity is honoured only while the server is still permitted and eligible. Failback policy
`Automatic` returns a failed-over session to the primary once it is healthy again; `EndOfSession` and `Manual` keep it
on the failover server until the session ends (`Manual` additionally keeps a recovered server out of rotation until an
administrator restores it).

## 4. Authentication and session

* Single entry `/_wings/login`. Company and username are used to **find** the identity, never to authorise.
* `IAuthenticationProvider` implementations: local credentials (PBKDF2), Wings Authentication service (HTTP adapter),
  OpenID Connect (extension). The gateway always re-resolves the company↔user membership from its own data.
* Identical generic failure for unknown company / unknown user / wrong password / disabled; dummy hash work for unknown
  users; per-IP rate limit and per-account lockout. MFA (TOTP) supported.
* On success the gateway issues a Data-Protection-encrypted cookie (`__Host-Wings.Session`) holding only a session id
  and the tenant claims, and creates a server-side session record (affinity per application, revocation flag).
  Tampering with the cookie invalidates it; there is no client-supplied company/server value anywhere in routing.
* Data Protection keys are stored in the routing database by default (`DataProtectionKeys`), or a file share / Redis,
  so any gateway can read any session cookie.

## 5. Proxying

YARP `IHttpForwarder` with a per-server `HttpMessageInvoker` (connection pooling, HTTP/2, optional mutual TLS, pinned
certificate validation). Request transforms remove every inbound `X-Wings-*` and configured identity header, the
gateway session cookie, and untrusted `X-Forwarded-*`; then add `X-Wings-Company-Id`, `X-Wings-User-Id`,
`X-Wings-Session`, `X-Wings-Application`, `X-Wings-Environment`, `X-Wings-Request-Id`, `X-Wings-Timestamp` and an
HMAC `X-Wings-Signature` that backends verify with their per-server secret. Response transforms remove `Server`,
`X-Powered-By`, rewrite internal `Location` headers to the public origin and strip `Domain` from `Set-Cookie`.

Redirect mode (per application environment) sends the browser to an allow-listed public server URL with a one-time
handoff token that the backend redeems server-to-server.

## 6. Configuration snapshot and caching

* Every routing-relevant write bumps `SystemSettings[RoutingConfigVersion]` inside the same transaction (EF
  `SaveChanges` interceptor), and publishes `wings:config` on Redis after commit.
* Gateways hold one immutable snapshot. They reload when notified or when a periodic version poll (default 15 s)
  sees a newer version. Snapshots are cached in Redis by version and written to a protected last-known-good file.
* No SQL query happens per request during normal operation.
* SQL unavailable → keep the last-known-good snapshot for `LastKnownGood:MaxAge` (default 30 min); after that the
  gateway fails closed with `503`. Redis unavailable → in-memory session/affinity on each gateway, DB polling for
  invalidation, hash-based affinity keeps decisions consistent.

## 7. Health monitoring

* **Active**: `GET {HealthCheckUrl}` with a per-server health key; JSON body reports readiness, version, CPU, memory,
  disk, connections, requests, queue, error rate, database connectivity. A ping is never enough.
* **Passive**: gateway transport failures/timeouts feed the same state machine.
* State machine: `Healthy → Degraded → Unhealthy → Recovering → Healthy`, with consecutive failure thresholds,
  degradation thresholds and a recovery stabilisation period. Every transition is written to `ServerHealthHistory` and
  the audit log.
* Only one monitor instance is active at a time (database lease); the others are hot standby. Gateways can also run
  local probes (development or when the monitor is unavailable).

## 8. Draining and maintenance

`Drain` stops new sessions immediately (config version bump); existing sessions keep affinity. The drain controller
watches active sessions/requests and moves the server to `Maintenance` when they reach zero or the drain timeout
expires. `Restore` returns it to `Active`; automatic restore after maintenance windows happens only after health checks
pass for the stabilisation period.

## 9. High availability and recovery

| Failure | Behaviour |
|---|---|
| One gateway | NLB removes it (`/_wings/health/ready`); sessions continue on another gateway (shared DP keys + Redis sessions). |
| Redis | Gateways continue with in-memory snapshot and local session records; invalidation falls back to DB polling; affinity for `PerUser`/`PerCompany` remains consistent; `PerSession` affinity may be re-resolved on another gateway. |
| SQL Server | Routing continues from last-known-good snapshot within `MaxAge`; logins via local credentials fail gracefully; audit/decision logs are buffered then dropped with a counter. |
| Monitor | Gateways keep last reported health plus passive checks; optionally enable gateway local probes. |
| Backend | Passive + active checks mark it unhealthy; failover only per policy. |

## 10. Database model

See [database.md](database.md). GUID v7 keys, `ConcurrencyStamp` optimistic concurrency, effective-dated
assignments (changes create new rows, so history and rollback are natural), composite unique constraints for tenant
identities.

## 11. Measured performance

Engine decision p99 1.6 µs over 5,000 companies / 200,000 users / 200 servers (target < 5 ms); one local gateway
proxied 42k req/s at p99 3.7 ms end to end. See [testing.md](testing.md).
