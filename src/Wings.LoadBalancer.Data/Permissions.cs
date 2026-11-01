namespace Wings.LoadBalancer.Data;

/// <summary>Permission codes checked by administrative endpoints, and the built-in roles that bundle them.</summary>
public static class Permissions
{
    public const string ServersRead = "servers.read";
    public const string ServersWrite = "servers.write";
    public const string ServersOperate = "servers.operate";
    public const string CompaniesRead = "companies.read";
    public const string CompaniesWrite = "companies.write";
    public const string AssignmentsRead = "assignments.read";
    public const string AssignmentsWrite = "assignments.write";
    public const string PoolsRead = "pools.read";
    public const string PoolsWrite = "pools.write";
    public const string PoliciesWrite = "policies.write";
    public const string RoutingSimulate = "routing.simulate";
    public const string MonitoringRead = "monitoring.read";
    public const string AuditRead = "audit.read";
    public const string AlertsRead = "alerts.read";
    public const string AlertsManage = "alerts.manage";
    public const string ReportsRead = "reports.read";
    public const string SessionsRead = "sessions.read";
    public const string SessionsRevoke = "sessions.revoke";
    public const string SettingsWrite = "settings.write";
    public const string AdministratorsManage = "admins.manage";
    public const string SecretsExport = "secrets.export";

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [ServersRead] = "View servers, health and metrics",
        [ServersWrite] = "Create, edit and retire servers",
        [ServersOperate] = "Enable, disable, drain and restore servers",
        [CompaniesRead] = "View companies and users",
        [CompaniesWrite] = "Create and edit companies, aliases and users",
        [AssignmentsRead] = "View company and user assignments",
        [AssignmentsWrite] = "Change company and user server assignments",
        [PoolsRead] = "View server pools",
        [PoolsWrite] = "Create and edit server pools",
        [PoliciesWrite] = "Edit failover and routing policies, applications",
        [RoutingSimulate] = "Run the routing simulator",
        [MonitoringRead] = "View dashboards and routing decisions",
        [AuditRead] = "View audit logs",
        [AlertsRead] = "View alerts",
        [AlertsManage] = "Acknowledge and resolve alerts, manage channels",
        [ReportsRead] = "Run and export reports",
        [SessionsRead] = "View active sessions",
        [SessionsRevoke] = "Revoke user sessions",
        [SettingsWrite] = "Change system settings",
        [AdministratorsManage] = "Manage administrators and roles",
        [SecretsExport] = "Export backend signing configuration",
    };

    public static readonly IReadOnlyDictionary<string, string[]> BuiltInRoles = new Dictionary<string, string[]>
    {
        ["SuperAdministrator"] = Descriptions.Keys.ToArray(),
        ["RoutingAdministrator"] =
        [
            ServersRead, ServersWrite, ServersOperate, CompaniesRead, CompaniesWrite, AssignmentsRead, AssignmentsWrite,
            PoolsRead, PoolsWrite, PoliciesWrite, RoutingSimulate, MonitoringRead, AuditRead, AlertsRead, AlertsManage,
            ReportsRead, SessionsRead, SessionsRevoke
        ],
        ["Operator"] =
        [
            ServersRead, ServersOperate, CompaniesRead, AssignmentsRead, PoolsRead, RoutingSimulate, MonitoringRead,
            AlertsRead, AlertsManage, ReportsRead, SessionsRead, SessionsRevoke
        ],
        ["ReadOnlyMonitor"] =
        [
            ServersRead, CompaniesRead, AssignmentsRead, PoolsRead, MonitoringRead, AlertsRead, ReportsRead, SessionsRead
        ],
        ["Auditor"] = [AuditRead, MonitoringRead, ReportsRead, ServersRead, CompaniesRead, AssignmentsRead],
    };
}
