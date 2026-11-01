# Configuration reference

Standard ASP.NET Core configuration: `appsettings.json` → `appsettings.{Environment}.json` → environment variables
(`Section__Key`) → command line. Secrets belong in environment variables or a vault, never in JSON files.
Production examples: `deploy/config/*.appsettings.Production.json`. Administrative runtime settings (retention,
alert thresholds, session idle time) live in the `SystemSettings` table and are edited in the admin UI.

## Shared (gateway, admin, monitor)

| Key | Default | Meaning |
|---|---|---|
| `Database:Provider` | `SqlServer` (`Sqlite` in Development) | Database engine |
| `ConnectionStrings:Wings` | — | Routing database |
| `Redis:Configuration` | empty | StackExchange.Redis connection string; empty = in-memory only |
| `Redis:KeyPrefix` | `wings:` | Key and channel prefix |
| `Redis:OperationTimeoutMs` | 250 | Per-operation timeout before falling back to memory |
| `BackendTrust:MasterKey` | — (**secret**) | Base64, ≥32 bytes. Per-server signing/health keys are derived from it. Same on every instance. |
| `BackendTrust:SignatureLifetimeSeconds` | 120 | Accepted clock skew for backend signature checks |
| `DataProtection:KeyPath` | empty | File share for the key ring (default: `DataProtectionKeys` table) |
| `DataProtection:UseRedis` | false | Store the key ring in Redis instead |
| `DataProtection:CertificateThumbprint` | empty | Encrypt the key ring at rest with this certificate |
| `DestinationPolicy:AllowedBackendHosts` | [] | Host patterns allowed for backend and health URLs (`*.wings.internal`, `app-server-*`) |
| `DestinationPolicy:AllowedBackendNetworks` | [] | CIDRs allowed for IP-literal backends |
| `DestinationPolicy:AllowedRedirectHosts` | [] | Hosts allowed for redirect-mode public URLs |
| `DestinationPolicy:AllowInsecureBackends` / `AllowLoopback` | false | Development only |
| `BackendConnections:ConnectTimeoutSeconds` | 5 | Backend TCP/TLS connect timeout |
| `BackendConnections:PooledConnectionLifetimeMinutes` | 5 | Connection pool recycling (DNS changes) |
| `BackendConnections:AllowUntrustedBackendCertificates` | false | Development only |
| `BackendConnections:ClientCertificateStore` | `LocalMachine` | Store for mTLS client certificates |
| `Health:*` | see below | Active/passive health checks |
| `Authentication:PasswordIterations` | 210000 | PBKDF2-SHA512 iterations |
| `Authentication:MaxFailedAttempts` / `LockoutMinutes` | 5 / 15 | Account lockout (exponential backoff after the first lockout) |
| `Seed:ApplyMigrations` | false (true in Development) | Run EF migrations at start-up |
| `Seed:SampleData` | false | Create sample tenants/servers (empty database only) |
| `Seed:AdminUsername` / `AdminPassword` | `admin` / — (**secret**) | First administrator (created only when none exists) |
| `Seed:SampleUserPassword` | — | Password for sample users |
| `Seed:SampleBackendUrlTemplate` | `http://localhost:{port}` | `{port}` = 5101–5108, `{name}` = `app-server-01` … |

## Gateway

| Key | Default | Meaning |
|---|---|---|
| `Gateway:InstanceName` | machine name | Shown on the dashboard, used in logs and decision logs |
| `Gateway:CookieName` | `__Host-Wings.Session` | Session cookie (`__Host-` requires HTTPS) |
| `Gateway:RequireHttpsCookies` | true | Secure cookies |
| `Gateway:SessionIdleMinutes` / `SessionAbsoluteHours` | 30 / 12 | Sliding idle timeout and absolute lifetime |
| `Gateway:MaxRequestBodyBytes` | 100 MB | Default body limit (per-application override in bindings) |
| `Gateway:RetryIdempotentRequests` | true | Retry safe, body-less requests once on a permitted alternative after a connect failure |
| `Gateway:StripRequestHeaders` / `StripResponseHeaders` | see code | Header hygiene lists (`X-Wings-*` is always stripped) |
| `Gateway:KnownProxies` / `KnownNetworks` | [] | Proxies trusted to set `X-Forwarded-For/Proto`. Empty = forwarded headers ignored. |
| `Gateway:LoginPermitsPerMinutePerIp` | 20 | Login rate limit |
| `Gateway:ProxyPermitsPerMinutePerIp` | 6000 | Request rate limit per client IP (0 = off) |
| `Gateway:HandoffTokenSeconds` | 60 | Redirect-mode handoff token lifetime |
| `Gateway:ShutdownTimeoutSeconds` | 30 | Graceful shutdown (readiness fails first, in-flight requests finish) |
| `Routing:LastKnownGoodMaxAgeMinutes` | 30 | Maximum time to route on a snapshot not confirmed by SQL (0 = unlimited, not recommended) |
| `Routing:TreatUnknownHealthAsHealthy` | false | Route to servers with no health data yet |
| `ConfigurationSync:VersionPollSeconds` | 15 | Fallback configuration version poll |
| `ConfigurationSync:SnapshotCacheMinutes` | 60 | Redis TTL of cached snapshots |
| `ConfigurationSync:LastKnownGoodPath` | `App_Data/routing-lkg.bin` | Data-Protection-encrypted last-known-good snapshot |
| `Health:GatewayMode` | `Shared` | `Shared` = use monitor-published health; `Local` = probe backends from this gateway |
| `Health:FeedPollSeconds` | 3 | How often shared health is read from SQL |
| `Health:SharedStateMaxAgeSeconds` | 120 | Shared health older than this counts as Unknown (not routable) |
| `Health:PassiveFailuresToQuarantine` / `PassiveQuarantineSeconds` | 3 / 30 | Passive (request-level) failure quarantine |
| `WingsAuthService:BaseUrl` / `ValidatePath` / `ApiKey` | — | Wings authentication service adapter |
| `Oidc:Authority` / `ClientId` / `ClientSecret` / `SubjectClaim` | — | Optional OpenID Connect provider (companies with `AuthenticationMode = Oidc`) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | Enables OTLP export of metrics (`Wings.Gateway` meter) and traces |

## Admin API

| Key | Default | Meaning |
|---|---|---|
| `Admin:CookieName` | `__Host-Wings.Admin` | Admin session cookie |
| `Admin:RequireHttpsCookies` | true | |
| `Admin:SessionMinutes` | 60 | Sliding admin session |
| `Admin:LoginPermitsPerMinute` | 10 | Admin login rate limit per IP |
| `Admin:GatewayUrl` | — | Public gateway URL used by the end-to-end routing test |
| `Monitor:RunInAdmin` | true | Run the health monitor and operations services inside the admin process |
| `Seed:RunOnAdmin` | true | Run the database initialiser at start-up (migrations if enabled, reference data, first admin) |
| `Notifications:*` | — | SMTP host/port/user/password, From, WebhookSecret, AdminPortalUrl, AllowPrivateWebhookTargets |

## Monitor (and admin when `Monitor:RunInAdmin`)

| Key | Default | Meaning |
|---|---|---|
| `Health:IntervalSeconds` | 10 | Active check interval |
| `Health:TimeoutSeconds` | 5 | Health request timeout |
| `Health:MetricsIntervalSeconds` | 60 | `ServerMetrics` sampling interval |
| `Health:MaxParallelChecks` | 16 | Concurrent probes |
| `Health:FailuresToUnhealthy` | 3 | Consecutive failures → Unhealthy |
| `Health:SuccessesToRecover` / `StabilizationSeconds` | 3 / 30 | Recovering → Healthy criteria |
| `Health:DegradedResponseTimeMs` / `DegradedCpuPercent` / `DegradedMemoryPercent` / `DegradedErrorRatePercent` | 1500 / 90 / 92 / 5 | Degraded thresholds |
| `Operations:DrainCheckSeconds` | 15 | Drain controller and maintenance scheduler tick |
| `Operations:AlertEvaluationSeconds` | 30 | Alert evaluation |
| `Operations:RetentionRunHours` | 6 | Retention clean-up |
| `Operations:GatewayHeartbeatStaleSeconds` | 120 | Gateway considered disconnected |

## SystemSettings (database, admin UI → Settings)

| Key | Default |
|---|---|
| `Retention.AuditDays` / `DecisionLogDays` / `MetricsDays` / `HealthHistoryDays` / `SessionDays` / `AlertDays` | 365 / 30 / 90 / 180 / 30 / 180 |
| `Alerts.CpuPercent` / `MemoryPercent` / `ErrorRatePercent` / `ResponseTimeMs` / `CapacityPercent` | 85 / 90 / 5 / 2000 / 85 |
| `Sessions.IdleMinutes` | 30 |
