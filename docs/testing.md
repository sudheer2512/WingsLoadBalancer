# Testing

```bash
dotnet test Wings.LoadBalancer.slnx
```

## Suites

| Project | What it covers | How |
|---|---|---|
| `tests/Wings.Routing.Core.Tests` (51 tests) | Routing engine, strategies, health state machine, load score, normalisation | Pure unit tests over an in-memory topology (`TestTopology`): ABC Motors (user + company dedicated), XYZ Motors (shared pool), DEF Trucks (dedicated, used to prove isolation) |
| `tests/Wings.IntegrationTests` (85 tests, including 30 security-primitive tests) | Gateway, admin API, monitor, persistence, resilience | Real Kestrel processes in one test host: SQLite database seeded with the sample tenants, **8 mock application servers** on random ports (each reports its name), 1–2 gateway instances, the admin API with the leader-elected monitor. DI scope validation is on. |
| `tests/Wings.Benchmarks` | Decision latency and throughput, end-to-end load | Console app (Release build) |

## Coverage against the specification (§25)

| Requirement | Test(s) |
|---|---|
| User-level routing priority | `ScenarioA_user_assignment_has_highest_priority`, `ScenarioA_user_dedicated_server_handles_every_request` |
| Company-level routing priority | `ScenarioB_*` (unit and integration) |
| Shared-pool routing | `ScenarioC_*`, `Per_user_affinity_is_stable_and_spreads_users`, strategy tests |
| Identical usernames in different companies | `ScenarioF_*` (unit and integration) |
| Unknown company / unknown user / disabled account | `Unknown_company_is_denied`, `Login_failures_are_indistinguishable`, `Disabled_user_and_suspended_company_are_denied`, `Disabling_a_user_cuts_off_existing_sessions` |
| Unhealthy dedicated server | `ScenarioD_failed_user_server_without_failover_policy_returns_no_healthy_destination`, `ScenarioD_dedicated_user_without_failover_gets_controlled_503` |
| Failover enabled / disabled | `ScenarioD_company_failover_to_permitted_secondary_then_automatic_failback`, `Failover_disabled_policy_never_moves_traffic`, `Max_failover_attempts_limits_candidates` |
| Unauthorised failover destination | `Unauthorised_failover_destination_dedicated_to_another_tenant_is_never_used`, `Dedicated_tenant_is_never_sent_to_unrelated_shared_pool_when_primary_fails`, `User_assignment_to_server_not_authorised_for_company_is_denied` |
| Stale cached assignment | `Stale_configuration_beyond_last_known_good_limit_fails_closed`, `Affinity_to_server_no_longer_permitted_is_ignored`, `ScenarioE_*` |
| Concurrent assignment changes | `Concurrent_assignment_changes_with_same_stamp_conflict` |
| Multiple gateway instances | `MultiGatewayTests` (shared cookies, identical decisions, version convergence) |
| Session affinity | `Draining_server_keeps_existing_sessions…`, `Automatic_failback…`, `End_of_session_failback…`, `ScenarioG_independent_engine_instances_make_identical_hash_decisions` |
| Server draining | `ScenarioI_*`, `Drain_moves_server_to_maintenance_when_sessions_end` |
| WebSocket handling | `Websocket_connections_are_proxied_to_the_assigned_server` |
| Large uploads | `Large_upload_and_download_stream_through` (24 MB up, 10 MB down, SHA-256 verified) |
| Backend connection failures | `Backend_connection_failure_retries_safe_request_on_permitted_server`, `Backend_failure_for_tenant_without_failover_never_reaches_other_servers` |
| SQL Server unavailability | `ScenarioJ_database_outage_keeps_authorised_traffic_on_last_known_good_configuration`, `ScenarioJ_last_known_good_expiry_fails_closed`, `Gateway_restart_without_database_uses_protected_last_known_good_file` |
| Redis unavailability | `Redis_unavailable_falls_back_to_in_memory_state` |
| Tenant isolation attacks | `ScenarioH_tampered_cookie_is_rejected`, `ScenarioH_spoofed_identity_and_forwarding_headers_are_replaced`, `Direct_backend_access_without_gateway_signature_is_refused`, `Unknown_host_is_not_routed`, `User_assignment_validation_blocks_isolation_and_unauthorised_servers`, `Ssrf_protection_rejects_metadata_and_disallowed_backend_urls`, `Return_url_cannot_redirect_off_site` |
| Admin security | `Unsafe_requests_without_xsrf_token_are_rejected`, `Read_only_monitor_role_cannot_change_configuration`, `Login_requires_antiforgery_token` |

## Acceptance scenarios (§29)

| Scenario | Status | Evidence |
|---|---|---|
| A — user1 on Server 01 | Pass | `GatewayRoutingTests.ScenarioA_*` |
| B — user2 on company Server 02 | Pass | `GatewayRoutingTests.ScenarioB_*` |
| C — XYZ to authorised shared pool | Pass | `GatewayRoutingTests.ScenarioC_*` |
| D — failover only when permitted | Pass | `FailoverAndMaintenanceTests.ScenarioD_*` |
| E — reassignment without restart | Pass | `ScenarioE_assignment_change_applies_to_new_sessions_without_restart`, `User_assignment_change_is_applied_by_the_gateway_and_can_be_rolled_back` |
| F — identical usernames | Pass | `ScenarioF_*` |
| G — multiple gateways consistent | Pass | `MultiGatewayTests` |
| H — tampered identity rejected | Pass | `ScenarioH_*` |
| I — maintenance/draining | Pass | `ScenarioI_*`, `Drain_moves_server_to_maintenance_when_sessions_end` |
| J — database outage, last-known-good | Pass | `DependencyOutageTests.ScenarioJ_*` |

## Performance (measured 2026-10-27, Apple M4, 10 cores, .NET 10)

Routing decision (engine only, cached snapshot, 5,000 companies / 200,000 users / 200 servers, mixed tenancy, 2% of servers unhealthy):

| Metric | Result | Target |
|---|---|---|
| p50 | 0.9 µs | |
| p99 | 1.6 µs | < 5 ms (initial benchmark target) |
| p99.9 | 6.3 µs | |
| Throughput, 1 thread | ~950,000 decisions/s | |
| Throughput, 10 threads | ~2,750,000 decisions/s | |

End-to-end through one gateway (debug build, all processes on the same laptop, shared-pool tenant, 64 concurrent
connections, 20 s): **42,389 req/s, p50 1.4 ms, p95 2.6 ms, p99 3.7 ms, 0 errors**. This includes proxy network and
backend time, so it is a single-host smoke figure, not a capacity number. Re-run on production-like hardware with
the gateway in Release mode and the NLB in front:

```bash
dotnet run -c Release --project tests/Wings.Benchmarks
```

```bash
dotnet run -c Release --project tests/Wings.Benchmarks -- load --gateway https://erp-test.example.com --company ABC --username loadtest --password '<test password>' --concurrency 128 --seconds 120
```

Note that the gateway's per-IP rate limiter (`Gateway:ProxyPermitsPerMinutePerIp`, default 6,000/min) will throttle a
single load generator; raise it or set it to 0 for load tests.

## Not covered here

* SQL Server and Redis themselves are not available on the development Mac, so the SQL Server migration scripts and the
  Redis code paths were not executed against real servers (Redis *unavailability* is tested). Run the Docker
  environment (`deploy/docker`) or a test SQL Server to cover them.
* IIS hosting and the Windows Service were not run here; use the checklist in [deployment-iis.md](deployment-iis.md).
* OIDC sign-in and the Wings authentication service adapter have no automated tests (no identity provider available).
* The Angular UI has no automated tests. It was checked with a production build, a match of all 98 client API calls
  against the OpenAPI document, and a browser walkthrough signed in as an administrator: dashboard (live SignalR
  decision feed), routing simulator, a user assignment saved with preview and then removed (the gateway picked up the
  change without a restart), drain → automatic maintenance → restore, audit log, reports and company pages. The
  bulk-import commit and the UI's end-to-end test button were exercised only through the API tests.
