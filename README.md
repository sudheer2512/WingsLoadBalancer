# Wings Intelligent Load Balancer

A multi-tenant routing gateway for Wings applications. Users open one public URL, sign in with their company and
username, and every request is transparently proxied to the application server that holds their company's data:
the user's dedicated server, the company's dedicated server or cluster, or an authorised shared pool. Failover goes
only to servers the tenant's policy explicitly permits.

```
Browser ─► NLB ─► Gateway ×N (YARP) ─► APP-SERVER-01 … APP-SERVER-NN
                       │ snapshot + health        ▲ /health/ready
           SQL Server ─┴─ Redis ─ Monitor ×2 ─────┘      Admin API + Angular UI
```

| Folder | Contents |
|---|---|
| `src/Wings.Routing.Core` | Routing policy engine (no YARP/ASP.NET dependency): resolution order, eligibility, isolation, failover, 7 load-balancing strategies, affinity, health state machine |
| `src/Wings.LoadBalancer.Data` | EF Core model (39 tables), SQL Server + SQLite migrations |
| `src/Wings.LoadBalancer.Infrastructure` | Redis/in-memory state, configuration sync and last-known-good, health monitoring, sessions, audit, alerts and notifications, authentication, SSRF guard, seeding |
| `src/Wings.Gateway` | Public gateway: login, sessions, YARP forwarding, header hygiene, passive health, metrics |
| `src/Wings.Admin.Api` | Admin REST API (OpenAPI + Scalar), SignalR monitoring hub, reports, hosts the Angular UI |
| `src/Wings.Monitor.Service` | Standalone health monitor / operations service (Windows Service or container) |
| `web/` | Angular admin application |
| `samples/Wings.MockBackend` | Sample application server (identifies itself, verifies gateway signatures, health, WebSockets, uploads) |
| `tests/` | Unit tests, multi-backend integration tests, benchmarks and load test |
| `database/` | SQL Server schema, reference data and sample data scripts |
| `deploy/` | Docker test environment, IIS `web.config`s, sample production configuration |
| `docs/` | Architecture, database, API, security, deployment, operations, testing |

## Quick start (macOS/Linux/Windows with the .NET 10 SDK)

```bash
./scripts/run-dev.sh
```

This builds everything and starts eight mock application servers (ports 5101–5108), the admin API (5290, seeds a
SQLite database with sample tenants and runs the health monitor) and a gateway (5280). Development passwords are
generated once into `App_Data/dev-credentials.env`.

* Gateway: <http://localhost:5280>. Sign in as company `ABC`, user `user1`, and you are served by APP-SERVER-01. Try
  `ABC/user2` (APP-SERVER-02), `XYZ/manager` (shared pool) and <http://localhost:5280/api/whoami>.
* Admin UI: <http://localhost:5290> (user `admin`). API reference: <http://localhost:5290/api/docs>.

Stop it with:

```bash
./scripts/stop-dev.sh
```

Docker (SQL Server, Redis, 2 gateways behind nginx, 2 monitors, 8 backends): see `deploy/docker/docker-compose.yml`.

## Tests

```bash
dotnet test Wings.LoadBalancer.slnx
```

136 automated tests: 51 engine unit tests and 85 integration and security tests (most run against real mock backends). They cover all ten
acceptance scenarios (A–J). Engine decision latency is p99 1.6 µs (target < 5 ms). Details: [docs/testing.md](docs/testing.md).

## Documentation

* [Architecture](docs/architecture.md)
* [Database model](docs/database.md)
* [Administration API](docs/api.md)
* [Security design](docs/security.md)
* [IIS deployment](docs/deployment-iis.md)
* [Operations runbook](docs/operations-runbook.md)
* [Testing and performance](docs/testing.md)
* [Configuration reference](docs/configuration.md)

## Integrating an application server

1. Implement `GET /health/ready` returning JSON metrics (contract: `BackendHealthReport` in
   `src/Wings.LoadBalancer.Infrastructure/Health/HealthProbe.cs`) and require the `X-Wings-Health-Key` header.
2. Trust `X-Wings-Company-Id` / `X-Wings-User-Id` only after verifying `X-Wings-Signature`. Copy
   `samples/Wings.MockBackend/GatewayIdentity.cs` and fetch the server's keys from `GET /api/servers/{id}/backend-config`.
3. Register the server in *Servers*, add it to a pool or assign companies/users, and watch it turn Healthy on the dashboard.
