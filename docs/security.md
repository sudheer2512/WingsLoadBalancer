# Security design

## Trust boundaries

1. **Client → gateway**: untrusted. Nothing the client sends (company id, username, server id, cookies other than
   the encrypted session ticket, `X-Forwarded-*`, `X-Wings-*` or any identity header) influences routing.
2. **Gateway → application server**: trusted only when the HMAC signature verifies. Application servers should be
   firewalled to accept traffic from gateways and monitors only.
3. **Admin network → admin API**: authenticated administrators with role-based permissions. Separate host, separate
   cookie, CSRF tokens.

## How each requirement is met

| Requirement | Implementation |
|---|---|
| Company/username never authorise by themselves | They only locate the identity. Authorisation = verified credentials (PBKDF2 / Wings auth service / OIDC) **and** an active membership in the routing database, re-checked at every sign-in. Every request also re-checks the company status and blocked-user list from the current snapshot. |
| No trust in client-supplied ids/headers | Routing input is built only from the encrypted session ticket + server-side session record + host binding. `X-Wings-*`, `X-Forwarded-*`, `Forwarded`, `X-Real-IP`, `X-Company-Id`, `X-User-Id`, `X-Remote-User`, `X-MS-CLIENT-PRINCIPAL*` … are removed from every request (`Gateway:StripRequestHeaders`). Forwarded headers are honoured only from configured `KnownProxies` / `KnownNetworks`. |
| Only the gateway injects identity | `X-Wings-Company-Id`, `-User-Id`, `-Session`, `-Application`, `-Environment`, `-Request-Id`, `-Timestamp`, `-Signature` = HMAC-SHA256 over those values plus method and path, with a per-server key derived (HKDF) from `BackendTrust:MasterKey`. Backends verify with `GatewayIdentity.Verify` (±2 min skew). |
| Tampered cookies | ASP.NET Core Data Protection (AES-256-CBC + HMACSHA256) ticket. Any modification fails decryption → 401. The ticket identity must also match the server-side session record. |
| Session revocation | Logout / admin revoke marks the session revoked in the session store and in `ActiveSessions`; every gateway polls revocations every 2 s and on Redis pub/sub. |
| Enumeration resistance | Identical 401 body for unknown company, unknown user, disabled user, locked account and wrong password; dummy PBKDF2 work for unknown accounts; pre-auth discovery answers "password" for unknown companies. Per-IP login rate limit and per-account lockout with backoff. |
| MFA | TOTP (RFC 6238) for users and administrators, secrets encrypted with Data Protection. OIDC providers can enforce their own MFA. |
| Tenant isolation | Engine checks on every candidate: environment, application compatibility, admin state, health, **dedicated-server ownership**, and for user assignments **company data authorisation**. Failover only to servers named by the policy. A missing assignment fails closed (never "any server"). Admin validation blocks the same violations at write time. |
| Fail closed on stale configuration | Snapshots not confirmed by SQL for `Routing:LastKnownGoodMaxAgeMinutes` → 503. |
| SSRF via admin-defined destinations | Backend/health URLs must match `DestinationPolicy` host patterns or CIDRs, https unless explicitly allowed, no credentials, health URL on the same host. At connect time every resolved IP is re-checked (blocks link-local/metadata `169.254.0.0/16`, multicast, unspecified, and loopback unless allowed), which also defeats DNS rebinding. Webhook targets: https only, private and link-local addresses blocked. |
| Open redirects | Login `returnUrl` must be a local path (`/…`, not `//`, `/\`, or `/_wings/…`). Redirect-mode targets must be on `DestinationPolicy:AllowedRedirectHosts`, https only. |
| Redirect-mode authentication continuity | Single-use 256-bit handoff token, 60 s TTL, stored hashed; redeemed server-to-server with an HMAC from the target server's key; bound to that server. |
| TLS to backends / mTLS | Chain validation always on (unless `AllowUntrustedBackendCertificates` in development). Optional certificate pinning (SHA-1 or SHA-256 thumbprint) and client certificates per server from the Windows certificate store. |
| Secure cookies | `__Host-` prefix, `Secure`, `HttpOnly`, `SameSite=Lax` (gateway; needed for OIDC returns) / `Strict` (admin), no `Domain`. Backend `Set-Cookie` headers have `Domain` removed so they stay on the public host. |
| CSRF | Gateway login/logout require an antiforgery header. Admin API requires `X-XSRF-TOKEN` on every unsafe method. |
| RBAC and read-only monitoring | 21 permissions, 5 built-in roles including `ReadOnlyMonitor` and `Auditor`. Fallback policy: authenticated. Administrator status re-checked every 5 minutes. |
| Secrets | No secrets in the database: backend keys are derived, SMTP/webhook/OIDC/API secrets come from configuration (environment, Key Vault). Session records and caches hold no passwords or tokens (`SaveTokens=false` for OIDC). |
| Request size limits | Kestrel/IIS global limit, per-application `MaxRequestBodyBytes` binding, IIS `maxAllowedContentLength`. |
| Rate limiting | Login: fixed window per IP. Proxy: token bucket per IP (`Gateway:ProxyPermitsPerMinutePerIp`; set to 0 when the NLB already rate-limits). |
| Audit and security events | Logins (success/failure), administrator logins, unauthorised routing attempts, configuration changes with previous/new values, session revocations, secret exports. Sensitive columns are masked. |
| Response hygiene | `Server`, `X-Powered-By`, `X-AspNet*` removed; internal `Location` headers rewritten to the public origin; error pages never name servers, hosts or exceptions (reference id only). Admin UI sends CSP, `X-Frame-Options: DENY`, `nosniff`, `no-referrer`. |
| Spreadsheet injection | CSV/XLSX exports prefix cells starting with `= + - @`. |

## Residual risks and recommendations

* An administrator with `assignments.write` can still grant a company access to a *shared/hybrid* server. Use roles to
  limit who holds that permission, and review `CompanyServerAuthorizations` changes in the audit log.
* OIDC discovery reveals that a federated company exists (inherent to federated sign-in). Disable OIDC companies on the
  public login page if that matters, and use dedicated sign-in hosts per IdP instead.
* `BackendTrust:MasterKey` protects every backend's identity. Store it in a vault, restrict who can read app pool
  environment variables, and rotate it with backends during maintenance.
* Last-known-good operation means a change made while SQL is down (impossible through the admin UI) or a revocation
  stored only in SQL can be missed for at most the configured window.
* Penetration-test before production (gateway header handling, cookie handling, admin API authorisation matrix).
