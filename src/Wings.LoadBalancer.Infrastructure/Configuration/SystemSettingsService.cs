using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data;

namespace Wings.LoadBalancer.Infrastructure.Configuration;

/// <summary>Known keys in the SystemSettings table, with defaults and descriptions.</summary>
public static class SettingKeys
{
    public const string AuditRetentionDays = "Retention.AuditDays";
    public const string DecisionLogRetentionDays = "Retention.DecisionLogDays";
    public const string MetricsRetentionDays = "Retention.MetricsDays";
    public const string HealthHistoryRetentionDays = "Retention.HealthHistoryDays";
    public const string SessionRetentionDays = "Retention.SessionDays";
    public const string AlertRetentionDays = "Retention.AlertDays";
    public const string AlertCpuPercent = "Alerts.CpuPercent";
    public const string AlertMemoryPercent = "Alerts.MemoryPercent";
    public const string AlertErrorRatePercent = "Alerts.ErrorRatePercent";
    public const string AlertResponseTimeMs = "Alerts.ResponseTimeMs";
    public const string AlertCapacityPercent = "Alerts.CapacityPercent";
    public const string SessionIdleMinutes = "Sessions.IdleMinutes";

    public static readonly IReadOnlyDictionary<string, (string Default, string Description)> Defaults =
        new Dictionary<string, (string, string)>
        {
            [AuditRetentionDays] = ("365", "Days to keep audit log entries"),
            [DecisionLogRetentionDays] = ("30", "Days to keep routing decision logs"),
            [MetricsRetentionDays] = ("90", "Days to keep server metrics samples"),
            [HealthHistoryRetentionDays] = ("180", "Days to keep server health transitions"),
            [SessionRetentionDays] = ("30", "Days to keep ended session records"),
            [AlertRetentionDays] = ("180", "Days to keep resolved alerts"),
            [AlertCpuPercent] = ("85", "CPU % that raises a high-CPU alert"),
            [AlertMemoryPercent] = ("90", "Memory % that raises a high-memory alert"),
            [AlertErrorRatePercent] = ("5", "Error rate % that raises an alert"),
            [AlertResponseTimeMs] = ("2000", "Health response time (ms) that raises an alert"),
            [AlertCapacityPercent] = ("85", "Session capacity % that raises a near-capacity alert"),
            [SessionIdleMinutes] = ("30", "Minutes without activity before a session stops counting as active"),
        };
}

public interface ISystemSettings
{
    Task<double> GetNumberAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class SystemSettingsService(WingsDbContext db) : ISystemSettings
{
    private Dictionary<string, string>? _cache;

    public async Task<double> GetNumberAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache ??= await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        var raw = _cache.TryGetValue(key, out var v) ? v : SettingKeys.Defaults.TryGetValue(key, out var d) ? d.Default : "0";
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
