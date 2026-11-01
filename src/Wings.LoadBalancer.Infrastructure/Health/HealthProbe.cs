using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.Routing.Health;
using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.LoadBalancer.Infrastructure.Health;

/// <summary>
/// Contract for <c>GET /health/ready</c> on Wings application servers. HTTP 200 means ready; any other status, a
/// timeout, a transport error or <c>status: "Unhealthy"</c> counts as a failed check.
/// </summary>
public sealed record BackendHealthReport
{
    public string? Status { get; init; }
    public string? ServerName { get; init; }
    public string? Version { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? DiskPercent { get; init; }
    public int? ActiveConnections { get; init; }
    public int? ActiveRequests { get; init; }
    public int? QueueLength { get; init; }
    public double? ErrorRatePercent { get; init; }
    public int? ActiveSessions { get; init; }
    public string? Database { get; init; }
}

public interface IHealthProbe
{
    Task<HealthObservation> ProbeAsync(ServerInfo server, CancellationToken cancellationToken);
}

public sealed class HealthProbe(
    IBackendHttpClientFactory clients,
    IBackendKeyProvider keys,
    IOptionsMonitor<HealthOptions> options,
    TimeProvider time) : IHealthProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public async Task<HealthObservation> ProbeAsync(ServerInfo server, CancellationToken cancellationToken)
    {
        var url = server.HealthCheckUrl is { Length: > 0 } h ? h : server.BaseUrl.TrimEnd('/') + "/health/ready";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.CurrentValue.TimeoutSeconds));
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(WingsHeaders.HealthKey, keys.GetHealthKey(server.Id));
            request.Headers.TryAddWithoutValidation(WingsHeaders.ServerId, server.Id.ToString());
            using var response = await clients.GetInvoker(server).SendAsync(request, timeout.Token);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            BackendHealthReport? report = null;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
            {
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                try
                {
                    report = await JsonSerializer.DeserializeAsync<BackendHealthReport>(body, Json, timeout.Token);
                }
                catch (JsonException)
                {
                    // A non-conforming body is not fatal; status code decides.
                }
            }

            var metrics = report is null ? null : new ServerMetricsSample(time.GetUtcNow(), report.CpuPercent,
                report.MemoryPercent, report.DiskPercent, report.ActiveConnections, report.ActiveRequests,
                report.QueueLength, report.ErrorRatePercent, elapsed, report.ActiveSessions,
                report.Database is null ? null : !report.Database.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase),
                report.Version);

            if (!response.IsSuccessStatusCode)
                return new HealthObservation(time.GetUtcNow(), false, elapsed, metrics, $"HTTP {(int)response.StatusCode}");
            if (string.Equals(report?.Status, "Unhealthy", StringComparison.OrdinalIgnoreCase))
                return new HealthObservation(time.GetUtcNow(), false, elapsed, metrics, "Application reported Unhealthy");
            return new HealthObservation(time.GetUtcNow(), true, elapsed, metrics);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthObservation(time.GetUtcNow(), false, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Failure: $"Timed out after {options.CurrentValue.TimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            return new HealthObservation(time.GetUtcNow(), false, Failure: $"Transport error: {ex.HttpRequestError}");
        }
    }
}
