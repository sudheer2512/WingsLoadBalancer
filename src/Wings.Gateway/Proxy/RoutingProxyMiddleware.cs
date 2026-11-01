using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Wings.Gateway.Sessions;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Health;
using Wings.LoadBalancer.Infrastructure.Monitoring;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.LoadBalancer.Infrastructure.Sessions;
using Wings.Routing.Engine;
using Wings.Routing.Model;
using Yarp.ReverseProxy.Forwarder;

namespace Wings.Gateway.Proxy;

/// <summary>
/// Terminal middleware for every application request: resolve the application from the host binding, require a
/// valid session, ask the routing engine for a destination using only trusted context, then forward with YARP.
/// </summary>
public sealed class RoutingProxyMiddleware(
    RequestDelegate next,
    IRoutingSnapshotProvider snapshots,
    IRoutingEngine engine,
    ServerStateStore state,
    GatewaySessionService sessions,
    IHttpForwarder forwarder,
    IBackendHttpClientFactory clients,
    IBackendKeyProvider keys,
    PassiveHealthPublisher passive,
    DecisionLogWriter decisionLog,
    SessionActivityWriter sessionActivity,
    HandoffService handoff,
    GatewayMetrics metrics,
    IOptions<GatewayOptions> options,
    TimeProvider time,
    ILogger<RoutingProxyMiddleware> logger)
{
    private readonly HashSet<string> _stripRequest = new(options.Value.StripRequestHeaders, StringComparer.OrdinalIgnoreCase);
    private readonly string _instance = options.Value.InstanceName ?? Environment.MachineName;

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var snapshot = snapshots.Current;
        if (snapshot is null)
        {
            await GatewayResponses.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "starting",
                "The service is starting. Please try again shortly.");
            return;
        }

        var binding = snapshot.ResolveBinding(context.Request.Host.Host, context.Request.Path.Value ?? "/");
        if (binding is null || !snapshot.Applications.TryGetValue(binding.ApplicationId, out var application))
        {
            await GatewayResponses.WriteAsync(context, StatusCodes.Status404NotFound, "not_found", "Not found.");
            return;
        }

        var (resolution, session) = await sessions.ResolveAsync(context);
        if (resolution != SessionResolution.Valid || session is null)
        {
            if (resolution is SessionResolution.Revoked or SessionResolution.Expired)
                await sessions.SignOutAsync(context, resolution.ToString());
            await GatewayResponses.ChallengeAsync(context);
            return;
        }

        var requestId = context.TraceIdentifier;
        session.Affinity.TryGetValue(application.Id, out var pinned);
        var request = new RoutingRequest
        {
            CompanyId = session.CompanyId,
            UserId = session.UserId,
            ApplicationId = application.Id,
            Environment = binding.Environment,
            AffinityServerId = pinned == Guid.Empty ? null : pinned
        };

        var decision = Decide(snapshot, request, out var decisionMicros);
        if (!decision.IsRouted)
        {
            await RefuseAsync(context, decision, request, session, requestId, decisionMicros);
            return;
        }

        if (binding.RoutingMode == ApplicationRoutingMode.Redirect)
        {
            session = await PersistAffinityAsync(session, application.Id, decision, request, requestId, decisionMicros, context);
            await handoff.RedirectAsync(context, snapshot.Servers[decision.ServerId!.Value], session, application, binding);
            return;
        }

        var maxBody = binding.MaxRequestBodyBytes ?? options.Value.MaxRequestBodyBytes;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            bodyLimit.MaxRequestBodySize = maxBody;

        var canRetry = options.Value.RetryIdempotentRequests && IsReplayable(context.Request);
        for (var attempt = 0; ; attempt++)
        {
            var server = snapshot.Servers[decision.ServerId!.Value];
            session = await PersistAffinityAsync(session, application.Id, decision, request, requestId, decisionMicros, context);

            var error = await ForwardAsync(context, server, session, application, binding, requestId);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (error is ForwarderError.None)
            {
                metrics.RecordProxy(elapsed, null);
                return;
            }

            var transportFailure = error is ForwarderError.Request or ForwarderError.RequestTimedOut;
            if (transportFailure && state.ObservePassive(server.Id, false, error.ToString()))
            {
                var until = state.QuarantinedUntil(server.Id) ?? time.GetUtcNow().AddSeconds(30);
                logger.LogWarning("{Server} quarantined after repeated transport failures", server.Name);
                _ = passive.PublishQuarantineAsync(server.Id, until);
            }
            metrics.RecordProxy(elapsed, error.ToString());

            if (!(canRetry && attempt == 0 && error == ForwarderError.Request && !context.Response.HasStarted))
            {
                if (!context.Response.HasStarted)
                    await GatewayResponses.WriteAsync(context, StatusCodes.Status502BadGateway, "bad_gateway",
                        "The application server could not complete the request. Please try again.", requestId);
                return;
            }

            // Retry once on another server permitted by the same policy (never outside the tenant's assignment).
            context.Response.Clear();
            request = request with { ExcludedServerIds = [server.Id], AffinityServerId = null };
            decision = Decide(snapshot, request, out decisionMicros);
            Log(decision, request, session, requestId, decisionMicros, "TransportFailure", context);
            if (!decision.IsRouted)
            {
                await RefuseAsync(context, decision, request, session, requestId, decisionMicros);
                return;
            }
        }
    }

    private RoutingDecision Decide(RoutingSnapshot snapshot, RoutingRequest request, out double micros)
    {
        var t = Stopwatch.GetTimestamp();
        var decision = engine.Route(snapshot, state, request);
        micros = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
        metrics.RecordDecision(decision, micros);
        return decision;
    }

    private async Task<ForwarderError> ForwardAsync(HttpContext context, ServerInfo server, SessionRecord session,
        ApplicationInfo application, ApplicationBindingInfo binding, string requestId)
    {
        var forwarding = new ForwardingContext(server, session, application, binding.Environment, requestId,
            keys.GetSigningKey(server.Id));
        var transformer = new WingsTransformer(forwarding, options.Value.CookieName, _stripRequest,
            options.Value.StripResponseHeaders, time);
        var config = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromSeconds(Math.Max(1, binding.RequestTimeoutSeconds)),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        state.BeginRequest(server.Id);
        metrics.RequestStarted();
        try
        {
            var error = await forwarder.SendAsync(context, server.BaseUrl, clients.GetInvoker(server), config, transformer);
            if (error is ForwarderError.None) state.ObservePassive(server.Id, true);
            else
            {
                var failure = context.Features.Get<IForwarderErrorFeature>();
                logger.LogWarning(failure?.Exception, "Forwarding to {Server} failed: {Error}", server.Name, error);
            }
            return error;
        }
        finally
        {
            state.EndRequest(server.Id);
            metrics.RequestEnded();
        }
    }

    /// <summary>Pins the session to the chosen server (copy-on-write; records are shared between requests).</summary>
    private async Task<SessionRecord> PersistAffinityAsync(SessionRecord session, Guid applicationId, RoutingDecision decision,
        RoutingRequest request, string requestId, double micros, HttpContext context)
    {
        var serverId = decision.ServerId!.Value;
        session.Affinity.TryGetValue(applicationId, out var existing);
        if (existing != serverId && decision.Affinity != AffinityMode.None)
        {
            var updated = session with
            {
                Affinity = new Dictionary<Guid, Guid>(session.Affinity) { [applicationId] = serverId },
                LastSeenAt = time.GetUtcNow()
            };
            await sessions.SaveAsync(updated);
            var kind = decision.IsFailback ? "Failback"
                : decision.IsFailover ? "Failover"
                : existing == Guid.Empty ? "NewSession" : "Reroute";
            Log(decision, request, updated, requestId, micros, kind, context);
            if (decision.IsFailover)
            {
                _ = context.RequestServices.GetRequiredService<IAuditTrail>().WriteAsync(new AuditEvent(AuditEvents.AutomaticFailover)
                {
                    CompanyId = session.CompanyId, UserId = session.UserId, ServerId = serverId,
                    Previous = existing == Guid.Empty ? null : existing.ToString(), New = serverId.ToString(),
                    Reason = decision.Reason, Actor = $"gateway:{_instance}", ActorType = "Gateway", CorrelationId = requestId,
                    Severity = AlertSeverity.Warning
                });
            }
            session = updated;
        }
        sessionActivity.Record(new SessionActivity(SessionKeys.Hash(session.SessionId), session.CompanyId, session.UserId,
            applicationId, serverId, time.GetUtcNow(), MaskAddress(context.Connection.RemoteIpAddress),
            context.Request.Headers.UserAgent.ToString()));
        return session;
    }

    private async Task RefuseAsync(HttpContext context, RoutingDecision decision, RoutingRequest request,
        SessionRecord session, string requestId, double micros)
    {
        Log(decision, request, session, requestId, micros, decision.Outcome.ToString(), context);
        if (decision.Outcome == RoutingOutcome.Denied)
        {
            _ = context.RequestServices.GetRequiredService<IAuditTrail>().WriteAsync(new AuditEvent(AuditEvents.UnauthorizedRoutingAttempt)
            {
                CompanyId = session.CompanyId, UserId = session.UserId, Reason = decision.Reason,
                Actor = $"gateway:{_instance}", ActorType = "Gateway", CorrelationId = requestId,
                SourceIp = MaskAddress(context.Connection.RemoteIpAddress), Severity = AlertSeverity.Warning
            });
            await sessions.SignOutAsync(context, "Denied");
            await GatewayResponses.WriteAsync(context, StatusCodes.Status403Forbidden, "access_denied",
                "Your account does not have access to this application.", requestId);
            return;
        }
        // NoAssignment, NoHealthyDestination and ConfigurationStale all fail closed with a controlled 503.
        context.Response.Headers.RetryAfter = "30";
        await GatewayResponses.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unavailable",
            "The service is temporarily unavailable for your organisation. Please try again shortly.", requestId);
    }

    private void Log(RoutingDecision decision, RoutingRequest request, SessionRecord session, string requestId,
        double micros, string kind, HttpContext context)
    {
        decisionLog.Enqueue(new RoutingDecisionLog
        {
            At = time.GetUtcNow(),
            CorrelationId = requestId,
            GatewayInstance = _instance,
            CompanyId = request.CompanyId,
            UserId = request.UserId,
            ApplicationId = request.ApplicationId,
            Environment = request.Environment,
            Outcome = decision.Outcome,
            ServerId = decision.ServerId,
            Source = decision.Source,
            PoolId = decision.PoolId,
            IsFailover = decision.IsFailover,
            IsFailback = decision.IsFailback,
            AffinityApplied = decision.AffinityApplied,
            Reason = decision.Reason.Length > 1000 ? decision.Reason[..1000] : decision.Reason,
            ConfigVersion = decision.ConfigVersion,
            DecisionMicroseconds = micros,
            SessionKey = SessionKeys.Hash(session.SessionId),
            EventKind = kind
        });
    }

    private static bool IsReplayable(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        && request.ContentLength is null or 0
        && !request.Headers.ContainsKey("Transfer-Encoding")
        && !request.Headers.ContainsKey("Upgrade");

    /// <summary>Keeps the network, drops the host part (privacy in session records).</summary>
    internal static string? MaskAddress(IPAddress? address)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            bytes[3] = 0;
            return new IPAddress(bytes) + "/24";
        }
        for (var i = 8; i < bytes.Length; i++) bytes[i] = 0;
        return new IPAddress(bytes) + "/64";
    }
}
