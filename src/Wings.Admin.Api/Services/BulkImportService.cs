using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.Routing;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Services;

public sealed record ImportRowResult(int Row, string Company, string Username, string Action, bool Valid, IReadOnlyList<string> Errors, string? Summary);

/// <summary>
/// Bulk user assignment import from CSV or Excel. Columns (header row, case-insensitive):
/// Company, Username, Action (Assign|Remove), PrimaryServer, SecondaryServer, FailoverPolicy, Application,
/// Environment, EffectiveFrom, EffectiveTo, Reason. Dry run validates every row; commit applies all rows atomically
/// and only when every row is valid.
/// </summary>
public sealed class BulkImportService(WingsDbContext db, AssignmentService assignments, IAuditTrail audit)
{
    public static readonly string[] Columns =
        ["Company", "Username", "Action", "PrimaryServer", "SecondaryServer", "FailoverPolicy", "Application", "Environment", "EffectiveFrom", "EffectiveTo", "Reason"];

    public async Task<object> ImportAsync(Stream file, string fileName, bool dryRun, string actor, CancellationToken ct)
    {
        var rows = fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? ReadExcel(file) : await ReadCsvAsync(file);
        if (rows.Count > 5000) throw new ValidationFailedException(["A single import is limited to 5,000 rows"]);

        var servers = await db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Name.ToUpperInvariant(), s => s.Id, ct);
        var policies = await db.FailoverPolicies.AsNoTracking().ToDictionaryAsync(p => p.Name.ToUpperInvariant(), p => p.Id, ct);
        var apps = await db.Applications.AsNoTracking().ToDictionaryAsync(a => a.Code.ToUpperInvariant(), a => a.Id, ct);
        var results = new List<ImportRowResult>();
        var plan = new List<(Guid UserId, UserAssignmentRequest? Assign, (Guid CompanyId, DeploymentEnvironment Env, Guid? App)? Remove)>();

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            string V(string column) => r.TryGetValue(column, out var v) ? v.Trim() : "";
            var errors = new List<string>();
            var companyInput = V("Company");
            var username = V("Username");
            var action = string.IsNullOrEmpty(V("Action")) ? "Assign" : V("Action");

            var normalizedCompany = IdentifierNormalizer.Normalize(companyInput);
            var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.NormalizedCode == normalizedCompany, ct)
                ?? await db.CompanyAliases.AsNoTracking().Where(a => a.NormalizedAlias == normalizedCompany).Select(a => a.Company).FirstOrDefaultAsync(ct);
            if (company is null) errors.Add($"Unknown company '{companyInput}'");
            var normalizedUser = IdentifierNormalizer.Normalize(username);
            var membership = company is null ? null : await db.UserCompanyMemberships.AsNoTracking()
                .FirstOrDefaultAsync(m => m.CompanyId == company.Id && m.NormalizedUsername == normalizedUser, ct);
            if (company is not null && membership is null) errors.Add($"User '{username}' not found in {company.Code}");

            var env = DeploymentEnvironment.Production;
            if (V("Environment") is { Length: > 0 } e && !Enum.TryParse(e, true, out env)) errors.Add($"Unknown environment '{e}'");
            Guid? app = null;
            if (V("Application") is { Length: > 0 } a)
            {
                if (apps.TryGetValue(a.ToUpperInvariant(), out var appId)) app = appId;
                else errors.Add($"Unknown application '{a}'");
            }

            if (action.Equals("Remove", StringComparison.OrdinalIgnoreCase))
            {
                if (errors.Count == 0) plan.Add((membership!.UserId, null, (company!.Id, env, app)));
                results.Add(new(i + 2, companyInput, username, "Remove", errors.Count == 0, errors, errors.Count == 0 ? "Remove user override" : null));
                continue;
            }
            if (!action.Equals("Assign", StringComparison.OrdinalIgnoreCase)) errors.Add($"Unknown action '{action}'");

            Guid? Server(string column, bool required)
            {
                var name = V(column);
                if (name.Length == 0)
                {
                    if (required) errors.Add($"{column} is required");
                    return null;
                }
                if (servers.TryGetValue(name.ToUpperInvariant(), out var id)) return id;
                errors.Add($"Unknown server '{name}'");
                return null;
            }
            var primary = Server("PrimaryServer", true);
            var secondary = Server("SecondaryServer", false);
            Guid? policy = null;
            if (V("FailoverPolicy") is { Length: > 0 } p)
            {
                if (policies.TryGetValue(p.ToUpperInvariant(), out var pid)) policy = pid;
                else errors.Add($"Unknown failover policy '{p}'");
            }
            DateTimeOffset? Date(string column)
            {
                var raw = V(column);
                if (raw.Length == 0) return null;
                if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d.ToUniversalTime();
                errors.Add($"{column} '{raw}' is not a valid date");
                return null;
            }
            var from = Date("EffectiveFrom");
            var to = Date("EffectiveTo");

            UserAssignmentRequest? request = null;
            if (errors.Count == 0)
            {
                request = new UserAssignmentRequest
                {
                    CompanyId = company!.Id, PrimaryServerId = primary!.Value, SecondaryServerId = secondary, FailoverPolicyId = policy,
                    ApplicationId = app, Environment = env, EffectiveFrom = from, EffectiveTo = to,
                    Reason = V("Reason") is { Length: > 0 } reason ? reason : "Bulk import"
                };
                errors.AddRange(await assignments.ValidateUserAssignmentAsync(request, from ?? DateTimeOffset.UtcNow, ct));
            }
            if (errors.Count == 0) plan.Add((membership!.UserId, request, null));
            results.Add(new(i + 2, companyInput, username, "Assign", errors.Count == 0, errors,
                errors.Count == 0 ? $"→ {V("PrimaryServer")}{(secondary is null ? "" : " / " + V("SecondaryServer"))} ({env})" : null));
        }

        var valid = results.All(r => r.Valid);
        var applied = 0;
        if (!dryRun && valid && plan.Count > 0)
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                applied = 0;
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                foreach (var step in plan)
                {
                    if (step.Assign is { } assign) await assignments.SetUserAssignmentAsync(step.UserId, assign, actor, ct);
                    else if (step.Remove is { } remove)
                    {
                        try
                        {
                            await assignments.RemoveUserAssignmentAsync(step.UserId, remove.CompanyId, remove.Env, remove.App, "Bulk import", null, ct);
                        }
                        catch (KeyNotFoundException)
                        {
                            // Nothing to remove.
                        }
                    }
                    applied++;
                }
                await tx.CommitAsync(ct);
            });
            await audit.WriteAsync(new AuditEvent(AuditEvents.BulkAssignmentImport) { Reason = $"{fileName}: {applied} row(s) applied" }, ct);
        }
        return new { dryRun, valid, total = results.Count, applied, rows = results };
    }

    private static async Task<List<Dictionary<string, string>>> ReadCsvAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        var records = ParseCsv(text);
        if (records.Count == 0) return [];
        var header = records[0].Select(h => h.Trim()).ToArray();
        return records.Skip(1).Where(r => r.Any(c => c.Trim().Length > 0)).Select(r =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length && i < r.Count; i++) d[header[i]] = r[i];
            return d;
        }).ToList();
    }

    /// <summary>RFC 4180 CSV parser (quoted fields, escaped quotes, embedded newlines).</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        if (rows.Count > 0 && rows[0].Count > 0) rows[0][0] = rows[0][0].TrimStart('﻿');
        return rows;
    }

    private static List<Dictionary<string, string>> ReadExcel(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var used = sheet.RangeUsed();
        if (used is null) return [];
        var header = used.FirstRow().Cells().Select(c => c.GetString().Trim()).ToArray();
        return used.RowsUsed().Skip(1).Select(r =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length; i++)
            {
                var cell = r.Cell(i + 1);
                d[header[i]] = cell.DataType == XLDataType.DateTime
                    ? new DateTimeOffset(DateTime.SpecifyKind(cell.GetDateTime(), DateTimeKind.Utc)).ToString("O")
                    : cell.GetString();
            }
            return d;
        }).ToList();
    }

    public static byte[] Template()
    {
        var sample = string.Join(',', Columns) + "\n" +
                     "ABC,user1,Assign,APP-SERVER-01,,,,Production,,,Dedicated server for finance\n" +
                     "ABC,user2,Remove,,,,,Production,,,Return to company default\n";
        return System.Text.Encoding.UTF8.GetBytes(sample);
    }
}
