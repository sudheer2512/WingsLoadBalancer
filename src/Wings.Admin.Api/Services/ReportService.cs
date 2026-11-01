using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Services;

public sealed record ReportTable(string Name, string Title, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

public sealed record ReportDefinition(string Name, string Title, string Description);

/// <summary>Operational reports with CSV and Excel export.</summary>
public sealed class ReportService(WingsDbContext db, TimeProvider time)
{
    public static readonly IReadOnlyList<ReportDefinition> Definitions =
    [
        new("server-utilization", "Server utilisation over time", "Hourly average CPU, memory, requests, response time and errors per server"),
        new("company-allocation", "Company-wise server allocation", "Current routing mode, servers, pools and failover policy per company"),
        new("user-allocation", "User-wise server allocation", "Current and scheduled user-level server assignments"),
        new("routing-decisions", "Routing decision history", "Logged routing decisions (new sessions, reroutes, failovers, refusals)"),
        new("failover-history", "Failover history", "Failover and failback decisions"),
        new("server-downtime", "Server downtime", "Unhealthy periods per server computed from health transitions"),
        new("peak-sessions", "Peak concurrent sessions", "Daily peak sessions per server"),
        new("requests-processed", "Requests processed", "Hourly requests, failed routing and failovers per gateway"),
        new("latency-distribution", "Latency distribution", "Distribution of backend response times and gateway latency"),
        new("error-rates", "Application error rates", "Hourly average error rate per server"),
        new("capacity-trends", "Capacity trends", "Daily average and peak session usage against capacity")
    ];

    public async Task<ReportTable> RunAsync(string name, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var end = to ?? time.GetUtcNow();
        var start = from ?? end.AddDays(-7);
        var def = Definitions.FirstOrDefault(d => d.Name == name) ?? throw new KeyNotFoundException($"Unknown report '{name}'");
        var servers = await db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s, ct);
        var companies = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c, ct);
        string S(Guid? id) => id is { } x && servers.TryGetValue(x, out var s) ? s.Name : "";
        string C(Guid? id) => id is { } x && companies.TryGetValue(x, out var c) ? c.Code : "";
        static DateTimeOffset Hour(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);
        static DateTimeOffset Day(DateTimeOffset t) => new(t.Year, t.Month, t.Day, 0, 0, 0, TimeSpan.Zero);

        switch (name)
        {
            case "server-utilization":
            case "error-rates":
            {
                var metrics = await db.ServerMetrics.AsNoTracking().Where(m => m.CapturedAt >= start && m.CapturedAt <= end).ToListAsync(ct);
                var groups = metrics.GroupBy(m => (m.ServerId, Hour(m.CapturedAt))).OrderBy(g => g.Key.Item2).ThenBy(g => S(g.Key.ServerId));
                if (name == "error-rates")
                    return new(name, def.Title, ["Hour (UTC)", "Server", "Avg error rate %", "Max error rate %", "Samples"],
                        groups.Select(g => new object?[] { g.Key.Item2, S(g.Key.ServerId), Avg(g.Select(m => m.ErrorRatePercent)),
                            Max(g.Select(m => m.ErrorRatePercent)), g.Count() }).ToList());
                return new(name, def.Title, ["Hour (UTC)", "Server", "Avg CPU %", "Avg memory %", "Avg active requests", "Avg response ms", "Avg error rate %", "Samples"],
                    groups.Select(g => new object?[] { g.Key.Item2, S(g.Key.ServerId), Avg(g.Select(m => m.CpuPercent)), Avg(g.Select(m => m.MemoryPercent)),
                        Avg(g.Select(m => (double?)m.ActiveRequests)), Avg(g.Select(m => m.ResponseTimeMs)), Avg(g.Select(m => m.ErrorRatePercent)), g.Count() }).ToList());
            }
            case "company-allocation":
            {
                var now = time.GetUtcNow();
                var ca = await db.CompanyServerAssignments.AsNoTracking().Include(a => a.FailoverPolicy).Include(a => a.ClusterPool)
                    .Where(a => a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now)).ToListAsync(ct);
                var pa = await db.SharedPoolAssignments.AsNoTracking().Include(a => a.Pool)
                    .Where(a => a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now)).ToListAsync(ct);
                var rows = companies.Values.OrderBy(c => c.Code).SelectMany(c =>
                {
                    var list = ca.Where(a => a.CompanyId == c.Id).Select(a => new object?[]
                    {
                        c.Code, c.Name, c.Status.ToString(), c.RoutingMode.ToString(), a.Environment.ToString(), S(a.PrimaryServerId),
                        S(a.SecondaryServerId), a.ClusterPool?.Name, a.FailoverPolicy?.Name, a.EffectiveFrom, a.EffectiveTo
                    }).Concat(pa.Where(a => a.CompanyId == c.Id).Select(a => new object?[]
                    {
                        c.Code, c.Name, c.Status.ToString(), "SharedPool", a.Environment.ToString(), "", "", a.Pool?.Name, "", a.EffectiveFrom, a.EffectiveTo
                    })).ToList();
                    return list.Count > 0 ? list : [new object?[] { c.Code, c.Name, c.Status.ToString(), c.RoutingMode.ToString(), "", "(no assignment)", "", "", "", null, null }];
                }).ToList();
                return new(name, def.Title, ["Company", "Name", "Status", "Routing mode", "Environment", "Primary", "Secondary", "Pool / cluster", "Failover policy", "Effective from", "Effective to"], rows);
            }
            case "user-allocation":
            {
                var now = time.GetUtcNow();
                var ua = await db.UserServerAssignments.AsNoTracking().Include(a => a.FailoverPolicy)
                    .Where(a => a.EffectiveTo == null || a.EffectiveTo > now).ToListAsync(ct);
                var usernames = await db.UserCompanyMemberships.AsNoTracking().ToDictionaryAsync(m => (m.CompanyId, m.UserId), m => m.Username, ct);
                return new(name, def.Title, ["Company", "Username", "Environment", "Primary", "Secondary", "Failover policy", "Enabled", "Effective from", "Effective to", "Status"],
                    ua.OrderBy(a => C(a.CompanyId)).ThenBy(a => usernames.GetValueOrDefault((a.CompanyId, a.UserId))).Select(a => new object?[]
                    {
                        C(a.CompanyId), usernames.GetValueOrDefault((a.CompanyId, a.UserId)), a.Environment.ToString(), S(a.PrimaryServerId),
                        S(a.SecondaryServerId), a.FailoverPolicy?.Name, a.IsEnabled, a.EffectiveFrom, a.EffectiveTo,
                        a.EffectiveFrom > now ? "Scheduled" : "Current"
                    }).ToList());
            }
            case "routing-decisions":
            case "failover-history":
            {
                var q = db.RoutingDecisionLogs.AsNoTracking().Where(d => d.At >= start && d.At <= end);
                if (name == "failover-history") q = q.Where(d => d.IsFailover || d.IsFailback);
                var rows = await q.OrderByDescending(d => d.At).Take(50_000).ToListAsync(ct);
                return new(name, def.Title, ["Time (UTC)", "Event", "Outcome", "Company", "Server", "Source", "Failover", "Failback", "Reason", "Gateway", "Config version", "Decision µs"],
                    rows.Select(d => new object?[] { d.At, d.EventKind, d.Outcome.ToString(), C(d.CompanyId), S(d.ServerId), d.Source.ToString(),
                        d.IsFailover, d.IsFailback, d.Reason, d.GatewayInstance, d.ConfigVersion, Math.Round(d.DecisionMicroseconds, 1) }).ToList());
            }
            case "server-downtime":
            {
                var history = await db.ServerHealthHistory.AsNoTracking().Where(h => h.At <= end).OrderBy(h => h.At).ToListAsync(ct);
                var rows = new List<object?[]>();
                foreach (var g in history.GroupBy(h => h.ServerId))
                {
                    DateTimeOffset? downSince = null;
                    var total = TimeSpan.Zero;
                    var incidents = 0;
                    foreach (var h in g)
                    {
                        if (h.ToState == HealthState.Unhealthy && downSince is null)
                        {
                            downSince = h.At < start ? start : h.At;
                            if (h.At >= start) incidents++;
                        }
                        else if (h.ToState != HealthState.Unhealthy && downSince is { } since)
                        {
                            if (h.At >= start) total += h.At - since;
                            downSince = null;
                        }
                    }
                    if (downSince is { } open) total += end - open;
                    var window = end - start;
                    rows.Add([S(g.Key), incidents, Math.Round(total.TotalMinutes, 1),
                        Math.Round(100 - total.TotalMinutes / Math.Max(1, window.TotalMinutes) * 100, 3), downSince is not null ? "Down" : "Up"]);
                }
                return new(name, def.Title, ["Server", "Incidents", "Downtime (min)", "Availability %", "Current"], rows.OrderBy(r => r[0]).ToList());
            }
            case "peak-sessions":
            case "capacity-trends":
            {
                var metrics = await db.ServerMetrics.AsNoTracking().Where(m => m.CapturedAt >= start && m.CapturedAt <= end)
                    .Select(m => new { m.ServerId, m.CapturedAt, m.ActiveSessions }).ToListAsync(ct);
                var groups = metrics.GroupBy(m => (m.ServerId, Day(m.CapturedAt))).OrderBy(g => g.Key.Item2).ThenBy(g => S(g.Key.ServerId));
                if (name == "peak-sessions")
                    return new(name, def.Title, ["Day (UTC)", "Server", "Peak sessions"],
                        groups.Select(g => new object?[] { g.Key.Item2, S(g.Key.ServerId), g.Max(m => m.ActiveSessions ?? 0) }).ToList());
                return new(name, def.Title, ["Day (UTC)", "Server", "Capacity", "Avg sessions", "Peak sessions", "Peak % of capacity"],
                    groups.Select(g =>
                    {
                        var capacity = servers.TryGetValue(g.Key.ServerId, out var s) ? s.MaxCapacity : 0;
                        var peak = g.Max(m => m.ActiveSessions ?? 0);
                        return new object?[] { g.Key.Item2, S(g.Key.ServerId), capacity, Math.Round(g.Average(m => m.ActiveSessions ?? 0), 1), peak,
                            capacity > 0 ? Math.Round(peak * 100.0 / capacity, 1) : null };
                    }).ToList());
            }
            case "requests-processed":
            {
                var samples = await db.GatewayTrafficSamples.AsNoTracking().Where(t => t.At >= start && t.At <= end).ToListAsync(ct);
                return new(name, def.Title, ["Hour (UTC)", "Gateway", "Requests", "Failed routing", "Failovers", "Avg latency ms"],
                    samples.GroupBy(t => (t.GatewayInstance, Hour(t.At))).OrderBy(g => g.Key.Item2).ThenBy(g => g.Key.GatewayInstance)
                        .Select(g => new object?[] { g.Key.Item2, g.Key.GatewayInstance, g.Sum(t => t.Requests), g.Sum(t => t.FailedRouting),
                            g.Sum(t => t.Failovers), Math.Round(g.Where(t => t.Requests > 0).Select(t => t.AverageLatencyMs).DefaultIfEmpty(0).Average(), 2) })
                        .ToList());
            }
            case "latency-distribution":
            {
                double[] edges = [50, 100, 250, 500, 1000, 2000, double.MaxValue];
                string Label(int i) => i == 0 ? "< 50 ms" : edges[i] == double.MaxValue ? $"≥ {edges[i - 1]:0} ms" : $"{edges[i - 1]:0}–{edges[i]:0} ms";
                var backend = await db.ServerMetrics.AsNoTracking().Where(m => m.CapturedAt >= start && m.CapturedAt <= end && m.ResponseTimeMs != null)
                    .Select(m => m.ResponseTimeMs!.Value).ToListAsync(ct);
                var gateway = await db.GatewayTrafficSamples.AsNoTracking().Where(t => t.At >= start && t.At <= end && t.Requests > 0)
                    .Select(t => t.AverageLatencyMs).ToListAsync(ct);
                int Count(IEnumerable<double> values, int i) => values.Count(v => v < edges[i] && (i == 0 || v >= edges[i - 1]));
                return new(name, def.Title, ["Bucket", "Backend health checks", "Gateway samples"],
                    Enumerable.Range(0, edges.Length).Select(i => new object?[] { Label(i), Count(backend, i), Count(gateway, i) }).ToList());
            }
        }
        throw new KeyNotFoundException($"Unknown report '{name}'");
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 2);
    }

    private static double? Max(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : Math.Round(list.Max(), 2);
    }

    public static byte[] ToCsv(ReportTable table)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', table.Columns.Select(Csv)));
        foreach (var row in table.Rows)
            sb.AppendLine(string.Join(',', row.Select(v => v is IFormattable and not DateTimeOffset ? Format(v) : Csv(Format(v)))));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static byte[] ToExcel(ReportTable table)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(table.Title.Length > 31 ? table.Title[..31] : table.Title);
        for (var c = 0; c < table.Columns.Count; c++) sheet.Cell(1, c + 1).Value = table.Columns[c];
        sheet.Row(1).Style.Font.Bold = true;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            for (var c = 0; c < table.Rows[r].Length; c++)
            {
                var cell = sheet.Cell(r + 2, c + 1);
                switch (table.Rows[r][c])
                {
                    case null: break;
                    case DateTimeOffset d: cell.Value = d.UtcDateTime; cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss"; break;
                    case int i: cell.Value = i; break;
                    case long l: cell.Value = l; break;
                    case double dbl: cell.Value = dbl; break;
                    case bool b: cell.Value = b; break;
                    case var v: cell.Value = SafeText(v.ToString()); break;
                }
            }
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(table.Rows.Count + 1, 200));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    // Spreadsheet formula injection protection for exported text.
    private static string SafeText(string? s) => s is { Length: > 0 } && "=+-@".Contains(s[0]) ? "'" + s : s ?? "";

    private static string Csv(string s)
    {
        s = SafeText(s);
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
