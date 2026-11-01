# Deploying on Windows Server with IIS

Target topology:

```
Internet ─► External NLB / F5 / Azure LB (TLS termination optional) ─► IIS: Gateway-01, Gateway-02 (Wings.Gateway)
Admin network ─► IIS: Wings.Admin.Api (admin UI + API)            Windows Service: Wings.Monitor.Service ×2 (leader + standby)
Shared: SQL Server (Always On recommended), Redis (optional but recommended), application servers (IIS, Wings apps)
```

## 1. Prerequisites (each IIS server)

1. Windows Server 2019 or later, IIS 10 with **WebSocket Protocol** (Server Manager → Web Server → Application
   Development) and **URL Rewrite** (optional).
2. ASP.NET Core **.NET 10 Hosting Bundle** (installs the ASP.NET Core Module V2). Run `iisreset` afterwards.
3. A TLS certificate for the public host (for example `erp.example.com`) in `LocalMachine\My`.
4. For mutual TLS to backends: the client certificate in `LocalMachine\My`, with private key read access granted to the
   app pool identity.

## 2. Database

1. Create an empty database `WingsLoadBalancer`. Recommended: `ALTER DATABASE WingsLoadBalancer SET READ_COMMITTED_SNAPSHOT ON;`
2. Run `database/sqlserver/001_schema.sql`, then `002_reference_data.sql`. Run `003_sample_data.sql` only for test environments.
3. Create least-privilege logins:
   * Gateway: `db_datareader` plus `INSERT/UPDATE` on `RoutingDecisionLogs`, `ActiveSessions`, `GatewayInstances`,
     `GatewayTrafficSamples`, `AuditLogs`, `DataProtectionKeys`, `Users` (login counters only).
   * Admin and monitor: `db_datareader`, `db_datawriter`.
   * Schema changes: a separate deployment login only.
   Use gMSA / Windows authentication (`Integrated Security=True`) where possible.

## 3. Secrets

Never put secrets in `appsettings.json`. Provide them as environment variables on the app pool
(`IIS Manager → Configuration Editor → system.applicationHost/applicationPools → environmentVariables`) or through
Azure Key Vault / a secret manager:

| Setting | Used by | Notes |
|---|---|---|
| `ConnectionStrings__Wings` | all | Prefer integrated security |
| `BackendTrust__MasterKey` | gateway, admin, monitor | 32+ random bytes, base64. Per-server signing and health keys are derived from it. Must be identical on every instance. |
| `Redis__Configuration` | all | e.g. `redis01:6380,ssl=true,password=…,abortConnect=false` |
| `Seed__AdminPassword` | admin (first start only) | Creates the first administrator when none exists. Remove after first sign-in. |
| `Notifications__SmtpPassword`, `Notifications__WebhookSecret` | admin/monitor | Optional |
| `WingsAuthService__ApiKey`, `Oidc__ClientSecret` | gateway | When those integrations are enabled |

Data Protection keys (session cookies) are stored in the `DataProtectionKeys` table by default, so every gateway can
read every cookie. To encrypt them at rest, set `DataProtection__CertificateThumbprint` (same certificate on all
servers) or `DataProtection__ProtectWithDpapiMachine=true` (single-server only).

## 4. Publish

```bash
dotnet publish src/Wings.Gateway/Wings.Gateway.csproj -c Release -o out/gateway
```

```bash
cd web && npm ci && npm run build && cd .. && dotnet publish src/Wings.Admin.Api/Wings.Admin.Api.csproj -c Release -o out/admin
```

```bash
dotnet publish src/Wings.Monitor.Service/Wings.Monitor.Service.csproj -c Release -r win-x64 --self-contained false -o out/monitor
```

## 5. IIS sites

For each gateway server:

1. Copy `out/gateway` to `D:\Wings\Gateway`. Create `D:\Wings\Gateway\App_Data` (last-known-good file) and grant the app pool **Modify** on it.
2. App pool `WingsGateway`: **No Managed Code**, Integrated pipeline, identity = gMSA or `ApplicationPoolIdentity`,
   **Start Mode = AlwaysRunning**, **Idle Time-out = 0**, **Regular recycling disabled** (recycle at a fixed off-peak time
   instead), Rapid-Fail Protection on.
3. Site `WingsGateway`: bindings `https:443` for `erp.example.com` (and staging hosts), require SNI, HTTP/2 enabled
   (default on Windows Server 2016+). **Preload Enabled = True**.
4. `web.config` is published with the app: in-process hosting, WebSockets on, 200 MB request limit, `Server` and
   `X-Powered-By` removed. Keep `maxAllowedContentLength` in step with `Gateway:MaxRequestBodyBytes`.
5. `appsettings.Production.json` (see `deploy/config/gateway.appsettings.Production.json`): set `Gateway:InstanceName`
   (Gateway-01, Gateway-02, …), `Health:GatewayMode=Shared`, `DestinationPolicy` allow-lists, and **`Gateway:KnownProxies`
   / `KnownNetworks` only if** the NLB terminates TLS and forwards `X-Forwarded-For`. With in-process hosting and a
   layer-4 NLB, leave them empty.

Admin site: same pattern on a separate host name (for example `wings-admin.corp.local`), reachable from the admin
network only. Restrict with IIS IP restrictions or the firewall. Set `Admin:GatewayUrl` for end-to-end tests, and
`Monitor:RunInAdmin=false` when the Windows Service monitor is installed.

## 6. Monitor Windows Service (two servers for standby)

```powershell
New-Item -ItemType Directory D:\Wings\Monitor
Copy-Item .\out\monitor\* D:\Wings\Monitor -Recurse
sc.exe create WingsLbMonitor binPath= "D:\Wings\Monitor\Wings.Monitor.Service.exe" start= delayed-auto obj= "DOMAIN\gmsa-wingslb$"
sc.exe failure WingsLbMonitor reset= 86400 actions= restart/5000/restart/5000/restart/30000
sc.exe start WingsLbMonitor
```

Provide `ConnectionStrings__Wings`, `BackendTrust__MasterKey`, `Redis__Configuration` as machine environment variables or in
`appsettings.Production.json` with an ACL. Only one instance is active at a time (leases `health-monitor` and
`operations` in `ServiceLeases`). The other takes over within about three check intervals.

## 7. Load balancer

* Health probe: `GET https://<gateway>/_wings/health/ready`. It returns 200 when the configuration is loaded and fresh, and
  503 while starting, draining (shutdown) or on stale configuration. Liveness: `/_wings/health/live`.
* Session persistence at the NLB is **not** required (cookies are valid on every gateway and routing is consistent).
* Idle timeout ≥ the longest WebSocket idle period (or enable keep-alives in the app).
* If the NLB terminates TLS, re-encrypt to the gateways and add the NLB's addresses to `Gateway:KnownProxies`.

## 8. Application servers

1. Expose `GET /health/ready` (JSON contract in `src/Wings.LoadBalancer.Infrastructure/Health/HealthProbe.cs`), and
   accept only requests carrying a valid `X-Wings-Health-Key`.
2. Trust `X-Wings-*` identity headers only after verifying `X-Wings-Signature` (reference implementation:
   `samples/Wings.MockBackend/GatewayIdentity.cs`). Get each server's keys from the admin API
   `GET /api/servers/{id}/backend-config` (needs `secrets.export`, audited).
3. Firewall application servers so that only gateways and monitors can reach them.

## 9. Validation checklist

* [ ] `/_wings/health/ready` returns 200 on each gateway; the NLB shows both gateways up.
* [ ] Admin UI → Dashboard shows both gateways online, in sync with the current configuration version.
* [ ] Routing Simulator returns the expected server for one user-dedicated, one company-dedicated and one shared-pool tenant.
* [ ] Routing Simulator → End-to-end test with a test account succeeds through the public URL.
* [ ] Drain a non-critical server: new sessions avoid it, and it moves to Maintenance when its sessions end.
* [ ] Stop one gateway app pool: traffic continues on the other without users signing in again.
* [ ] Stop Redis: traffic continues (gateways log the in-memory fallback). Restore Redis.
* [ ] Fail a backend health check: failover happens only where the policy permits (audit log `AutomaticFailover`).
