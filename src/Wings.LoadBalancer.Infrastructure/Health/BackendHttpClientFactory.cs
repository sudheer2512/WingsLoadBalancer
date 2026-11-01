using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.Routing.Model;

namespace Wings.LoadBalancer.Infrastructure.Health;

public sealed class BackendConnectionOptions
{
    public const string Section = "BackendConnections";

    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int PooledConnectionLifetimeMinutes { get; set; } = 5;
    public int MaxConnectionsPerServer { get; set; } = int.MaxValue;
    /// <summary>Development only: accept backend certificates that fail chain validation.</summary>
    public bool AllowUntrustedBackendCertificates { get; set; }
    public StoreLocation ClientCertificateStore { get; set; } = StoreLocation.LocalMachine;
}

/// <summary>
/// One pooled <see cref="HttpMessageInvoker"/> per backend server: connection pooling, HTTP/2, optional mutual TLS,
/// certificate pinning and connect-time address checks (DNS rebinding / SSRF protection).
/// </summary>
public interface IBackendHttpClientFactory
{
    HttpMessageInvoker GetInvoker(ServerInfo server);
}

public sealed class BackendHttpClientFactory(
    IOptions<BackendConnectionOptions> options,
    IOptionsMonitor<DestinationPolicyOptions> destinationPolicy,
    ILogger<BackendHttpClientFactory> logger) : IBackendHttpClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _invokers = new();

    public HttpMessageInvoker GetInvoker(ServerInfo server)
    {
        var key = $"{server.Id}|{server.BaseUrl}|{server.ClientCertificateThumbprint}|{server.BackendCertificateThumbprint}";
        return _invokers.GetOrAdd(key, newKey =>
        {
            // Retire handlers for previous configurations of the same server.
            foreach (var stale in _invokers.Keys.Where(k => k.StartsWith(server.Id + "|", StringComparison.Ordinal) && k != newKey))
            {
                if (_invokers.TryRemove(stale, out var old)) _ = DisposeLaterAsync(old);
            }
            return new HttpMessageInvoker(CreateHandler(server), disposeHandler: true);
        });
    }

    private SocketsHttpHandler CreateHandler(ServerInfo server)
    {
        var o = options.Value;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(o.PooledConnectionLifetimeMinutes),
            ConnectTimeout = TimeSpan.FromSeconds(o.ConnectTimeoutSeconds),
            MaxConnectionsPerServer = o.MaxConnectionsPerServer,
            ActivityHeadersPropagator = new ReverseProxyPropagator(),
            ConnectCallback = ConnectAsync
        };

        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (errors != SslPolicyErrors.None && !o.AllowUntrustedBackendCertificates)
            {
                logger.LogWarning("Backend {Server} certificate rejected: {Errors}", server.Name, errors);
                return false;
            }
            if (server.BackendCertificateThumbprint is { Length: > 0 } pin && certificate is not null)
            {
                using var cert = new X509Certificate2(certificate);
                var matches = string.Equals(cert.Thumbprint, pin, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), pin, StringComparison.OrdinalIgnoreCase);
                if (!matches) logger.LogWarning("Backend {Server} certificate does not match the pinned thumbprint", server.Name);
                return matches;
            }
            return true;
        };

        if (server.ClientCertificateThumbprint is { Length: > 0 } thumbprint)
        {
            var certificate = LoadCertificate(thumbprint, o.ClientCertificateStore);
            if (certificate is null)
                logger.LogError("Client certificate {Thumbprint} for {Server} not found; mutual TLS will fail", thumbprint, server.Name);
            else
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
        }
        return handler;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var policy = destinationPolicy.CurrentValue;
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);
        var permitted = addresses.Where(a => DestinationValidator.IsAddressPermitted(a, policy)).ToArray();
        if (permitted.Length == 0)
            throw new HttpRequestException($"Resolved addresses for {host} are not permitted backend destinations");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static X509Certificate2? LoadCertificate(string thumbprint, StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return found.Count > 0 ? found[0] : null;
    }

    private static async Task DisposeLaterAsync(HttpMessageInvoker invoker)
    {
        // Let in-flight requests finish on the old handler.
        await Task.Delay(TimeSpan.FromMinutes(2));
        invoker.Dispose();
    }

    public void Dispose()
    {
        foreach (var invoker in _invokers.Values) invoker.Dispose();
    }

    /// <summary>Same propagation behaviour YARP recommends: replace incoming trace headers with our activity.</summary>
    private sealed class ReverseProxyPropagator : DistributedContextPropagator
    {
        private readonly DistributedContextPropagator _inner = CreateDefaultPropagator();
        public override IReadOnlyCollection<string> Fields => _inner.Fields;

        public override void Inject(System.Diagnostics.Activity? activity, object? carrier, PropagatorSetterCallback? setter)
        {
            if (carrier is HttpRequestMessage message)
            {
                foreach (var field in _inner.Fields) message.Headers.Remove(field);
            }
            _inner.Inject(activity, carrier, setter);
        }

        public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
            _inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

        public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) =>
            _inner.ExtractBaggage(carrier, getter);
    }
}
