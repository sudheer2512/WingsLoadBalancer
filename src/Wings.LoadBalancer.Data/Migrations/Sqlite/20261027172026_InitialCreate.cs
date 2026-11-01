using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wings.LoadBalancer.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActiveSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionKey = table.Column<string>(type: "TEXT", maxLength: 88, nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GatewayInstance = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ClientAddress = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Administrators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    MfaEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MfaSecretProtected = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FailedLoginCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LockoutEndAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastLoginAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Administrators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FirstRaisedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRaisedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Occurrences = table.Column<int>(type: "INTEGER", nullable: false),
                    AcknowledgedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ResolutionNote = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinimumServerVersion = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    ActorType = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PreviousValue = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    SourceIp = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FriendlyName = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Xml = table.Column<string>(type: "TEXT", maxLength: -1, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GatewayInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceName = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastHeartbeatAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ConfigVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestsPerSecond = table.Column<double>(type: "REAL", nullable: false),
                    AverageLatencyMs = table.Column<double>(type: "REAL", nullable: false),
                    AverageDecisionMicroseconds = table.Column<double>(type: "REAL", nullable: false),
                    FailedRoutingLastInterval = table.Column<long>(type: "INTEGER", nullable: false),
                    FailoversLastInterval = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveRequests = table.Column<int>(type: "INTEGER", nullable: false),
                    RedisAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    DatabaseAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigurationStale = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GatewayTrafficSamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GatewayInstance = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Requests = table.Column<long>(type: "INTEGER", nullable: false),
                    AverageLatencyMs = table.Column<double>(type: "REAL", nullable: false),
                    AverageDecisionMicroseconds = table.Column<double>(type: "REAL", nullable: false),
                    FailedRouting = table.Column<long>(type: "INTEGER", nullable: false),
                    Failovers = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayTrafficSamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    MinimumSeverity = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoutingDecisionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    GatewayInstance = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PoolId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsFailover = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsFailback = table.Column<bool>(type: "INTEGER", nullable: false),
                    AffinityApplied = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ConfigVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    DecisionMicroseconds = table.Column<double>(type: "REAL", nullable: false),
                    SessionKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    EventKind = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingDecisionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoutingPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowDegradedServers = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultAffinity = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CpuWeight = table.Column<double>(type: "REAL", nullable: false),
                    MemoryWeight = table.Column<double>(type: "REAL", nullable: false),
                    ActiveRequestsWeight = table.Column<double>(type: "REAL", nullable: false),
                    ResponseTimeWeight = table.Column<double>(type: "REAL", nullable: false),
                    ErrorRateWeight = table.Column<double>(type: "REAL", nullable: false),
                    ResponseTimeCeilingMs = table.Column<double>(type: "REAL", nullable: false),
                    SessionIdleTimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionAbsoluteTimeoutHours = table.Column<int>(type: "INTEGER", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerHealthCurrent",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    StateChangedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastCheckAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastHeartbeatAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseTimeMs = table.Column<double>(type: "REAL", nullable: true),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: true),
                    MemoryPercent = table.Column<double>(type: "REAL", nullable: true),
                    DiskPercent = table.Column<double>(type: "REAL", nullable: true),
                    ActiveConnections = table.Column<int>(type: "INTEGER", nullable: true),
                    ActiveRequests = table.Column<int>(type: "INTEGER", nullable: true),
                    QueueLength = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorRatePercent = table.Column<double>(type: "REAL", nullable: true),
                    ActiveSessions = table.Column<int>(type: "INTEGER", nullable: true),
                    DatabaseHealthy = table.Column<bool>(type: "INTEGER", nullable: true),
                    ReportedVersion = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    AwaitingManualRestore = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerHealthCurrent", x => x.ServerId);
                });

            migrationBuilder.CreateTable(
                name: "ServerHealthHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromState = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ToState = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ReportedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerHealthHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: true),
                    MemoryPercent = table.Column<double>(type: "REAL", nullable: true),
                    DiskPercent = table.Column<double>(type: "REAL", nullable: true),
                    ActiveConnections = table.Column<int>(type: "INTEGER", nullable: true),
                    ActiveRequests = table.Column<int>(type: "INTEGER", nullable: true),
                    QueueLength = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorRatePercent = table.Column<double>(type: "REAL", nullable: true),
                    ResponseTimeMs = table.Column<double>(type: "REAL", nullable: true),
                    ActiveSessions = table.Column<int>(type: "INTEGER", nullable: true),
                    DatabaseHealthy = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    PublicUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    HealthCheckUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Region = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ServerType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CpuCores = table.Column<int>(type: "INTEGER", nullable: true),
                    RamGb = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxCapacity = table.Column<int>(type: "INTEGER", nullable: false),
                    ApplicationVersion = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    AdminState = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Weight = table.Column<int>(type: "INTEGER", nullable: false),
                    RequireManualRestore = table.Column<bool>(type: "INTEGER", nullable: false),
                    DrainStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DrainTimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientCertificateThumbprint = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    BackendCertificateThumbprint = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    RetiredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceLeases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Holder = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLeases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    MfaEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MfaSecretProtected = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    FailedLoginCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LockoutEndAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastLoginAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationEnvironments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PublicHost = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    PathPrefix = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    RoutingMode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRequestBodyBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationEnvironments_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdministratorRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdministratorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdministratorRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdministratorRoles_Administrators_AdministratorId",
                        column: x => x.AdministratorId,
                        principalTable: "Administrators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdministratorRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    ServicePlan = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    RoutingMode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DefaultEnvironment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    MaxConcurrentUsers = table.Column<int>(type: "INTEGER", nullable: true),
                    AuthenticationMode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RoutingPolicyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Companies_RoutingPolicies_RoutingPolicyId",
                        column: x => x.RoutingPolicyId,
                        principalTable: "RoutingPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartsAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndsAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DrainTimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoRestore = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceWindows_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServerApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerApplications_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServerApplications_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CapacityReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReservedSessions = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapacityReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CapacityReservations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CapacityReservations_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CompanyAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    NormalizedAlias = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyAliases_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CompanyServerAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    GrantedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    GrantedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyServerAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyServerAuthorizations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompanyServerAuthorizations_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserCompanyMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ExternalSubject = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCompanyMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCompanyMemberships_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserCompanyMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CompanyServerAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrimaryServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecondaryServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClusterPoolId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClusterMode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FailoverPolicyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EffectiveFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectiveTo = table.Column<long>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    SupersededById = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyServerAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyServerAssignments_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyServerAssignments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyServerAssignments_Servers_PrimaryServerId",
                        column: x => x.PrimaryServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyServerAssignments_Servers_SecondaryServerId",
                        column: x => x.SecondaryServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FailoverPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    FailoverEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailoverPoolId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FailbackPolicy = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    MaxFailoverAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    FailoverPriority = table.Column<int>(type: "INTEGER", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailoverPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerPools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Region = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Algorithm = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Affinity = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FailoverPolicyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MaxCapacity = table.Column<int>(type: "INTEGER", nullable: true),
                    EligiblePlans = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerPools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerPools_FailoverPolicies_FailoverPolicyId",
                        column: x => x.FailoverPolicyId,
                        principalTable: "FailoverPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserServerAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrimaryServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecondaryServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FailoverPolicyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AllowCompanyRouteFallback = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EffectiveFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectiveTo = table.Column<long>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    SupersededById = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserServerAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserServerAssignments_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserServerAssignments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserServerAssignments_FailoverPolicies_FailoverPolicyId",
                        column: x => x.FailoverPolicyId,
                        principalTable: "FailoverPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserServerAssignments_Servers_PrimaryServerId",
                        column: x => x.PrimaryServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserServerAssignments_Servers_SecondaryServerId",
                        column: x => x.SecondaryServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserServerAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServerPoolApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PoolId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerPoolApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerPoolApplications_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServerPoolApplications_ServerPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "ServerPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServerPoolMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PoolId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WeightOverride = table.Column<int>(type: "INTEGER", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    AcceptingNewSessions = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerPoolMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerPoolMembers_ServerPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "ServerPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServerPoolMembers_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SharedPoolAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PoolId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EffectiveFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectiveTo = table.Column<long>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    SupersededById = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedPoolAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedPoolAssignments_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SharedPoolAssignments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SharedPoolAssignments_ServerPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "ServerPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActiveSessions_CompanyId_EndedAt",
                table: "ActiveSessions",
                columns: new[] { "CompanyId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ActiveSessions_RevokedAt",
                table: "ActiveSessions",
                column: "RevokedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ActiveSessions_ServerId_EndedAt",
                table: "ActiveSessions",
                columns: new[] { "ServerId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ActiveSessions_SessionKey_ApplicationId",
                table: "ActiveSessions",
                columns: new[] { "SessionKey", "ApplicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdministratorRoles_AdministratorId_RoleId",
                table: "AdministratorRoles",
                columns: new[] { "AdministratorId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdministratorRoles_RoleId",
                table: "AdministratorRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Administrators_NormalizedUsername",
                table: "Administrators",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_DedupKey_Status",
                table: "Alerts",
                columns: new[] { "DedupKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Status_LastRaisedAt",
                table: "Alerts",
                columns: new[] { "Status", "LastRaisedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationEnvironments_ApplicationId_Environment",
                table: "ApplicationEnvironments",
                columns: new[] { "ApplicationId", "Environment" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationEnvironments_PublicHost_PathPrefix",
                table: "ApplicationEnvironments",
                columns: new[] { "PublicHost", "PathPrefix" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Applications_Code",
                table: "Applications",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_At",
                table: "AuditLogs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CompanyId_At",
                table: "AuditLogs",
                columns: new[] { "CompanyId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EventType_At",
                table: "AuditLogs",
                columns: new[] { "EventType", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_CapacityReservations_CompanyId",
                table: "CapacityReservations",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CapacityReservations_ServerId_CompanyId",
                table: "CapacityReservations",
                columns: new[] { "ServerId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_Name",
                table: "Companies",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_NormalizedCode",
                table: "Companies",
                column: "NormalizedCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_RoutingPolicyId",
                table: "Companies",
                column: "RoutingPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAliases_CompanyId",
                table: "CompanyAliases",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAliases_NormalizedAlias",
                table: "CompanyAliases",
                column: "NormalizedAlias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyServerAssignments_ApplicationId",
                table: "CompanyServerAssignments",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyServerAssignments_ClusterPoolId",
                table: "CompanyServerAssignments",
                column: "ClusterPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyServerAssignments_CompanyId_Environment_ApplicationId_EffectiveFrom",
                table: "CompanyServerAssignments",
                columns: new[] { "CompanyId", "Environment", "ApplicationId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyServerAssignments_CompanyId_IsEnabled_EffectiveTo",
                table: "CompanyServerAssignments",
                columns: new[] { "CompanyId", "IsEnabled", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyServerAssignments_FailoverPolicyId",
                table: "CompanyServerAssignments",
                column: "FailoverPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyServerAssignments_PrimaryServerId",
                table: "CompanyServerAssignments",
                column: "PrimaryServerId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyServerAssignments_SecondaryServerId",
                table: "CompanyServerAssignments",
                column: "SecondaryServerId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyServerAuthorizations_CompanyId_ServerId",
                table: "CompanyServerAuthorizations",
                columns: new[] { "CompanyId", "ServerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyServerAuthorizations_ServerId",
                table: "CompanyServerAuthorizations",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationVersions_ChangedAt",
                table: "ConfigurationVersions",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FailoverPolicies_FailoverPoolId",
                table: "FailoverPolicies",
                column: "FailoverPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_FailoverPolicies_Name",
                table: "FailoverPolicies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GatewayInstances_InstanceName",
                table: "GatewayInstances",
                column: "InstanceName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GatewayTrafficSamples_At_GatewayInstance",
                table: "GatewayTrafficSamples",
                columns: new[] { "At", "GatewayInstance" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_ServerId_StartsAt",
                table: "MaintenanceWindows",
                columns: new[] { "ServerId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_Status_StartsAt",
                table: "MaintenanceWindows",
                columns: new[] { "Status", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannels_Name",
                table: "NotificationChannels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Code",
                table: "Permissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_PermissionId",
                table: "RolePermissions",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleId_PermissionId",
                table: "RolePermissions",
                columns: new[] { "RoleId", "PermissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoutingDecisionLogs_At",
                table: "RoutingDecisionLogs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingDecisionLogs_CompanyId_At",
                table: "RoutingDecisionLogs",
                columns: new[] { "CompanyId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingDecisionLogs_Outcome_At",
                table: "RoutingDecisionLogs",
                columns: new[] { "Outcome", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingDecisionLogs_ServerId_At",
                table: "RoutingDecisionLogs",
                columns: new[] { "ServerId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingPolicies_Name",
                table: "RoutingPolicies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerApplications_ApplicationId",
                table: "ServerApplications",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerApplications_ServerId_ApplicationId",
                table: "ServerApplications",
                columns: new[] { "ServerId", "ApplicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerHealthHistory_ServerId_At",
                table: "ServerHealthHistory",
                columns: new[] { "ServerId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ServerMetrics_CapturedAt",
                table: "ServerMetrics",
                column: "CapturedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ServerMetrics_ServerId_CapturedAt",
                table: "ServerMetrics",
                columns: new[] { "ServerId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServerPoolApplications_ApplicationId",
                table: "ServerPoolApplications",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerPoolApplications_PoolId_ApplicationId",
                table: "ServerPoolApplications",
                columns: new[] { "PoolId", "ApplicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerPoolMembers_PoolId_ServerId",
                table: "ServerPoolMembers",
                columns: new[] { "PoolId", "ServerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerPoolMembers_ServerId",
                table: "ServerPoolMembers",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerPools_FailoverPolicyId",
                table: "ServerPools",
                column: "FailoverPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerPools_Name",
                table: "ServerPools",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Servers_Environment_AdminState",
                table: "Servers",
                columns: new[] { "Environment", "AdminState" });

            migrationBuilder.CreateIndex(
                name: "IX_Servers_Name",
                table: "Servers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLeases_Name",
                table: "ServiceLeases",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedPoolAssignments_ApplicationId",
                table: "SharedPoolAssignments",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedPoolAssignments_CompanyId_IsEnabled_EffectiveTo",
                table: "SharedPoolAssignments",
                columns: new[] { "CompanyId", "IsEnabled", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_SharedPoolAssignments_CompanyId_PoolId_Environment_ApplicationId_EffectiveFrom",
                table: "SharedPoolAssignments",
                columns: new[] { "CompanyId", "PoolId", "Environment", "ApplicationId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedPoolAssignments_PoolId",
                table: "SharedPoolAssignments",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_Key",
                table: "SystemSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMemberships_CompanyId_ExternalSubject",
                table: "UserCompanyMemberships",
                columns: new[] { "CompanyId", "ExternalSubject" },
                unique: true,
                filter: "\"ExternalSubject\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMemberships_CompanyId_NormalizedUsername",
                table: "UserCompanyMemberships",
                columns: new[] { "CompanyId", "NormalizedUsername" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMemberships_CompanyId_UserId",
                table: "UserCompanyMemberships",
                columns: new[] { "CompanyId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMemberships_UserId",
                table: "UserCompanyMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_UserServerAssignments_ApplicationId",
                table: "UserServerAssignments",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_UserServerAssignments_CompanyId_IsEnabled_EffectiveTo",
                table: "UserServerAssignments",
                columns: new[] { "CompanyId", "IsEnabled", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_UserServerAssignments_CompanyId_UserId_Environment_ApplicationId_EffectiveFrom",
                table: "UserServerAssignments",
                columns: new[] { "CompanyId", "UserId", "Environment", "ApplicationId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserServerAssignments_FailoverPolicyId",
                table: "UserServerAssignments",
                column: "FailoverPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserServerAssignments_PrimaryServerId",
                table: "UserServerAssignments",
                column: "PrimaryServerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserServerAssignments_SecondaryServerId",
                table: "UserServerAssignments",
                column: "SecondaryServerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserServerAssignments_UserId",
                table: "UserServerAssignments",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CompanyServerAssignments_FailoverPolicies_FailoverPolicyId",
                table: "CompanyServerAssignments",
                column: "FailoverPolicyId",
                principalTable: "FailoverPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CompanyServerAssignments_ServerPools_ClusterPoolId",
                table: "CompanyServerAssignments",
                column: "ClusterPoolId",
                principalTable: "ServerPools",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FailoverPolicies_ServerPools_FailoverPoolId",
                table: "FailoverPolicies",
                column: "FailoverPoolId",
                principalTable: "ServerPools",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServerPools_FailoverPolicies_FailoverPolicyId",
                table: "ServerPools");

            migrationBuilder.DropTable(
                name: "ActiveSessions");

            migrationBuilder.DropTable(
                name: "AdministratorRoles");

            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "ApplicationEnvironments");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "CapacityReservations");

            migrationBuilder.DropTable(
                name: "CompanyAliases");

            migrationBuilder.DropTable(
                name: "CompanyServerAssignments");

            migrationBuilder.DropTable(
                name: "CompanyServerAuthorizations");

            migrationBuilder.DropTable(
                name: "ConfigurationVersions");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "GatewayInstances");

            migrationBuilder.DropTable(
                name: "GatewayTrafficSamples");

            migrationBuilder.DropTable(
                name: "MaintenanceWindows");

            migrationBuilder.DropTable(
                name: "NotificationChannels");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "RoutingDecisionLogs");

            migrationBuilder.DropTable(
                name: "ServerApplications");

            migrationBuilder.DropTable(
                name: "ServerHealthCurrent");

            migrationBuilder.DropTable(
                name: "ServerHealthHistory");

            migrationBuilder.DropTable(
                name: "ServerMetrics");

            migrationBuilder.DropTable(
                name: "ServerPoolApplications");

            migrationBuilder.DropTable(
                name: "ServerPoolMembers");

            migrationBuilder.DropTable(
                name: "ServiceLeases");

            migrationBuilder.DropTable(
                name: "SharedPoolAssignments");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "UserCompanyMemberships");

            migrationBuilder.DropTable(
                name: "UserServerAssignments");

            migrationBuilder.DropTable(
                name: "Administrators");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Applications");

            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropTable(
                name: "Servers");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "RoutingPolicies");

            migrationBuilder.DropTable(
                name: "FailoverPolicies");

            migrationBuilder.DropTable(
                name: "ServerPools");
        }
    }
}
