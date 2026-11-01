using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;

namespace Wings.LoadBalancer.Infrastructure.Monitoring;

public interface ILeaseService
{
    /// <summary>Acquires or renews a named lease. Returns true when this holder owns it until the returned expiry.</summary>
    Task<bool> TryAcquireAsync(string name, string holder, TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>Database lease with optimistic concurrency, so only one monitor instance is active at a time.</summary>
public sealed class DbLeaseService(IServiceScopeFactory scopes, TimeProvider time) : ILeaseService
{
    public async Task<bool> TryAcquireAsync(string name, string holder, TimeSpan duration, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        var now = time.GetUtcNow();
        try
        {
            var lease = await db.ServiceLeases.FirstOrDefaultAsync(l => l.Name == name, ct);
            if (lease is null)
            {
                db.ServiceLeases.Add(new ServiceLease { Name = name, Holder = holder, ExpiresAt = now + duration });
            }
            else if (lease.Holder == holder || lease.ExpiresAt <= now)
            {
                lease.Holder = holder;
                lease.ExpiresAt = now + duration;
                lease.ConcurrencyStamp = Guid.NewGuid();
            }
            else
            {
                return false;
            }
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another instance won the race (concurrency conflict or unique index).
            return false;
        }
    }
}

public static class LeaseHolder
{
    public static string Create()
    {
        var value = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return value.Length <= 120 ? value : value[..120];
    }
}
