using Wings.Routing.Model;

namespace Wings.LoadBalancer.Infrastructure.Monitoring;

public enum IssueSeverity { Warning, Error }

public sealed record ConfigurationIssue(IssueSeverity Severity, string Code, string Message, Guid? CompanyId = null, Guid? ServerId = null);

/// <summary>
/// Static checks over a routing configuration: isolation conflicts, assignments to unusable servers, environment
/// mismatches, empty pools and tenants without any route. Used by the admin API and the alert evaluator.
/// </summary>
public static class ConfigurationValidator
{
    public static IReadOnlyList<ConfigurationIssue> Validate(RoutingSnapshot s, DateTimeOffset now)
    {
        var issues = new List<ConfigurationIssue>();
        bool Current(IScopedAssignment a) => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo is null || a.EffectiveTo > now);

        foreach (var (serverId, owners) in s.DedicatedOwners)
        {
            if (s.Servers.TryGetValue(serverId, out var server) && server.Type == ServerType.Dedicated && owners.Count > 1)
                issues.Add(new(IssueSeverity.Error, "DedicatedServerShared",
                    $"Dedicated server {server.Name} is assigned to {owners.Count} companies", ServerId: serverId));
        }

        void CheckServer(Guid serverId, DeploymentEnvironment env, Guid companyId, string role)
        {
            if (!s.Servers.TryGetValue(serverId, out var server))
            {
                issues.Add(new(IssueSeverity.Error, "AssignmentToMissingServer",
                    $"{role} references a retired or unknown server {serverId}", companyId, serverId));
                return;
            }
            if (server.Environment != env)
                issues.Add(new(IssueSeverity.Error, "EnvironmentMismatch",
                    $"{role} for {env} points to {server.Name} in {server.Environment}", companyId, serverId));
            if (server.AdminState is ServerAdminState.Disabled or ServerAdminState.Maintenance)
                issues.Add(new(IssueSeverity.Warning, "AssignmentToUnavailableServer",
                    $"{role} points to {server.Name}, which is {server.AdminState}", companyId, serverId));
        }

        foreach (var a in s.CompanyAssignments.Values.SelectMany(x => x).Where(Current))
        {
            CheckServer(a.PrimaryServerId, a.Environment, a.CompanyId, "Company primary");
            if (a.SecondaryServerId is { } sec) CheckServer(sec, a.Environment, a.CompanyId, "Company secondary");
            if (a.ClusterPoolId is { } pool && !s.Pools.ContainsKey(pool))
                issues.Add(new(IssueSeverity.Error, "MissingClusterPool", "Company assignment references a missing cluster pool", a.CompanyId));
            if (a.FailoverPolicyId is { } fp && !s.FailoverPolicies.ContainsKey(fp))
                issues.Add(new(IssueSeverity.Error, "MissingFailoverPolicy", "Company assignment references a missing failover policy", a.CompanyId));
        }

        foreach (var a in s.UserAssignments.Values.SelectMany(x => x).Where(Current))
        {
            CheckServer(a.PrimaryServerId, a.Environment, a.CompanyId, "User primary");
            if (a.SecondaryServerId is { } sec) CheckServer(sec, a.Environment, a.CompanyId, "User secondary");
        }

        foreach (var pool in s.Pools.Values)
        {
            if (pool.Members.Count == 0)
                issues.Add(new(IssueSeverity.Warning, "EmptyPool", $"Pool '{pool.Name}' has no members"));
            foreach (var m in pool.Members)
            {
                if (s.Servers.TryGetValue(m.ServerId, out var server) && server.Environment != pool.Environment)
                    issues.Add(new(IssueSeverity.Error, "PoolEnvironmentMismatch",
                        $"Pool '{pool.Name}' ({pool.Environment}) contains {server.Name} ({server.Environment})", ServerId: server.Id));
            }
            if (pool.Members.Count > 0 && pool.Members.All(m => !m.AcceptingNewSessions))
                issues.Add(new(IssueSeverity.Warning, "PoolClosed", $"No member of pool '{pool.Name}' accepts new sessions"));
        }

        foreach (var spa in s.SharedPoolAssignments.Values.SelectMany(x => x).Where(Current))
        {
            if (!s.Pools.TryGetValue(spa.PoolId, out var pool))
                issues.Add(new(IssueSeverity.Error, "MissingPool", "Shared pool assignment references a missing pool", spa.CompanyId));
            else if (pool.Environment != spa.Environment)
                issues.Add(new(IssueSeverity.Error, "EnvironmentMismatch",
                    $"Shared pool assignment for {spa.Environment} points to pool '{pool.Name}' in {pool.Environment}", spa.CompanyId));
        }

        foreach (var company in s.Companies.Values.Where(c => c.Status == CompanyStatus.Active))
        {
            var hasRoute = s.GetCompanyAssignments(company.Id).Any(Current) || s.GetSharedPoolAssignments(company.Id).Any(Current);
            if (!hasRoute)
                issues.Add(new(IssueSeverity.Warning, "CompanyWithoutRoute",
                    $"Active company {company.Code} has no current company or shared-pool assignment", company.Id));
            if (company.RoutingMode == CompanyRoutingMode.SharedPool && s.GetCompanyAssignments(company.Id).Any(Current))
                issues.Add(new(IssueSeverity.Warning, "RoutingModeMismatch",
                    $"Company {company.Code} is marked SharedPool but has a dedicated assignment", company.Id));
        }

        foreach (var server in s.Servers.Values.Where(x => x.AdminState == ServerAdminState.Active && string.IsNullOrEmpty(x.HealthCheckUrl)))
            issues.Add(new(IssueSeverity.Warning, "DefaultHealthUrl", $"{server.Name} has no explicit health check URL (using /health/ready)", ServerId: server.Id));

        return issues;
    }
}
