IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ActiveSessions] (
        [Id] uniqueidentifier NOT NULL,
        [SessionKey] nvarchar(88) NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [ServerId] uniqueidentifier NULL,
        [GatewayInstance] nvarchar(400) NULL,
        [StartedAt] datetimeoffset NOT NULL,
        [LastSeenAt] datetimeoffset NOT NULL,
        [EndedAt] datetimeoffset NULL,
        [EndReason] nvarchar(400) NULL,
        [RevokedAt] datetimeoffset NULL,
        [RevokedBy] nvarchar(400) NULL,
        [ClientAddress] nvarchar(400) NULL,
        [UserAgent] nvarchar(400) NULL,
        CONSTRAINT [PK_ActiveSessions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [Administrators] (
        [Id] uniqueidentifier NOT NULL,
        [Username] nvarchar(400) NOT NULL,
        [NormalizedUsername] nvarchar(400) NOT NULL,
        [DisplayName] nvarchar(400) NOT NULL,
        [Email] nvarchar(400) NULL,
        [PasswordHash] nvarchar(400) NULL,
        [MfaEnabled] bit NOT NULL,
        [MfaSecretProtected] nvarchar(400) NULL,
        [Status] nvarchar(40) NOT NULL,
        [FailedLoginCount] int NOT NULL,
        [LockoutEndAt] datetimeoffset NULL,
        [LastLoginAt] datetimeoffset NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ModifiedAt] datetimeoffset NOT NULL,
        [ModifiedBy] nvarchar(400) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Administrators] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [Alerts] (
        [Id] uniqueidentifier NOT NULL,
        [Type] nvarchar(400) NOT NULL,
        [Severity] nvarchar(40) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [DedupKey] nvarchar(400) NOT NULL,
        [Title] nvarchar(400) NOT NULL,
        [Message] nvarchar(4000) NULL,
        [ServerId] uniqueidentifier NULL,
        [CompanyId] uniqueidentifier NULL,
        [FirstRaisedAt] datetimeoffset NOT NULL,
        [LastRaisedAt] datetimeoffset NOT NULL,
        [Occurrences] int NOT NULL,
        [AcknowledgedAt] datetimeoffset NULL,
        [AcknowledgedBy] nvarchar(400) NULL,
        [ResolvedAt] datetimeoffset NULL,
        [ResolvedBy] nvarchar(400) NULL,
        [ResolutionNote] nvarchar(400) NULL,
        CONSTRAINT [PK_Alerts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [Applications] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(400) NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(400) NULL,
        [IsEnabled] bit NOT NULL,
        [MinimumServerVersion] nvarchar(400) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Applications] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] uniqueidentifier NOT NULL,
        [At] datetimeoffset NOT NULL,
        [Actor] nvarchar(400) NOT NULL,
        [ActorType] nvarchar(400) NOT NULL,
        [EventType] nvarchar(400) NOT NULL,
        [EntityType] nvarchar(400) NULL,
        [EntityId] uniqueidentifier NULL,
        [CompanyId] uniqueidentifier NULL,
        [UserId] uniqueidentifier NULL,
        [ServerId] uniqueidentifier NULL,
        [PreviousValue] nvarchar(max) NULL,
        [NewValue] nvarchar(max) NULL,
        [Reason] nvarchar(2000) NULL,
        [CorrelationId] nvarchar(400) NULL,
        [SourceIp] nvarchar(400) NULL,
        [Severity] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ConfigurationVersions] (
        [Id] uniqueidentifier NOT NULL,
        [Version] bigint NOT NULL,
        [ChangedAt] datetimeoffset NOT NULL,
        [ChangedBy] nvarchar(400) NULL,
        [Summary] nvarchar(2000) NULL,
        CONSTRAINT [PK_ConfigurationVersions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [DataProtectionKeys] (
        [Id] int NOT NULL IDENTITY,
        [FriendlyName] nvarchar(400) NULL,
        [Xml] nvarchar(max) NULL,
        CONSTRAINT [PK_DataProtectionKeys] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [GatewayInstances] (
        [Id] uniqueidentifier NOT NULL,
        [InstanceName] nvarchar(400) NOT NULL,
        [Host] nvarchar(400) NULL,
        [Version] nvarchar(400) NULL,
        [StartedAt] datetimeoffset NOT NULL,
        [LastHeartbeatAt] datetimeoffset NOT NULL,
        [ConfigVersion] bigint NOT NULL,
        [RequestsPerSecond] float NOT NULL,
        [AverageLatencyMs] float NOT NULL,
        [AverageDecisionMicroseconds] float NOT NULL,
        [FailedRoutingLastInterval] bigint NOT NULL,
        [FailoversLastInterval] bigint NOT NULL,
        [ActiveRequests] int NOT NULL,
        [RedisAvailable] bit NOT NULL,
        [DatabaseAvailable] bit NOT NULL,
        [ConfigurationStale] bit NOT NULL,
        CONSTRAINT [PK_GatewayInstances] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [GatewayTrafficSamples] (
        [Id] uniqueidentifier NOT NULL,
        [GatewayInstance] nvarchar(400) NOT NULL,
        [At] datetimeoffset NOT NULL,
        [IntervalSeconds] int NOT NULL,
        [Requests] bigint NOT NULL,
        [AverageLatencyMs] float NOT NULL,
        [AverageDecisionMicroseconds] float NOT NULL,
        [FailedRouting] bigint NOT NULL,
        [Failovers] bigint NOT NULL,
        CONSTRAINT [PK_GatewayTrafficSamples] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationChannels] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Type] nvarchar(40) NOT NULL,
        [Target] nvarchar(2000) NOT NULL,
        [MinimumSeverity] nvarchar(40) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_NotificationChannels] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [Permissions] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(400) NOT NULL,
        [Description] nvarchar(400) NULL,
        CONSTRAINT [PK_Permissions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [Roles] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(400) NULL,
        [IsSystem] bit NOT NULL,
        CONSTRAINT [PK_Roles] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [RoutingDecisionLogs] (
        [Id] uniqueidentifier NOT NULL,
        [At] datetimeoffset NOT NULL,
        [CorrelationId] nvarchar(400) NULL,
        [GatewayInstance] nvarchar(400) NULL,
        [CompanyId] uniqueidentifier NULL,
        [UserId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Environment] nvarchar(40) NULL,
        [Outcome] nvarchar(40) NOT NULL,
        [ServerId] uniqueidentifier NULL,
        [Source] nvarchar(40) NOT NULL,
        [PoolId] uniqueidentifier NULL,
        [IsFailover] bit NOT NULL,
        [IsFailback] bit NOT NULL,
        [AffinityApplied] bit NOT NULL,
        [Reason] nvarchar(1000) NULL,
        [ConfigVersion] bigint NOT NULL,
        [DecisionMicroseconds] float NOT NULL,
        [SessionKey] nvarchar(400) NULL,
        [EventKind] nvarchar(400) NULL,
        CONSTRAINT [PK_RoutingDecisionLogs] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [RoutingPolicies] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [IsDefault] bit NOT NULL,
        [AllowDegradedServers] bit NOT NULL,
        [DefaultAffinity] nvarchar(40) NOT NULL,
        [CpuWeight] float NOT NULL,
        [MemoryWeight] float NOT NULL,
        [ActiveRequestsWeight] float NOT NULL,
        [ResponseTimeWeight] float NOT NULL,
        [ErrorRateWeight] float NOT NULL,
        [ResponseTimeCeilingMs] float NOT NULL,
        [SessionIdleTimeoutMinutes] int NOT NULL,
        [SessionAbsoluteTimeoutHours] int NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_RoutingPolicies] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ServerHealthCurrent] (
        [ServerId] uniqueidentifier NOT NULL,
        [State] nvarchar(40) NOT NULL,
        [StateChangedAt] datetimeoffset NULL,
        [LastCheckAt] datetimeoffset NULL,
        [LastHeartbeatAt] datetimeoffset NULL,
        [LastReason] nvarchar(400) NULL,
        [ConsecutiveFailures] int NOT NULL,
        [ResponseTimeMs] float NULL,
        [CpuPercent] float NULL,
        [MemoryPercent] float NULL,
        [DiskPercent] float NULL,
        [ActiveConnections] int NULL,
        [ActiveRequests] int NULL,
        [QueueLength] int NULL,
        [ErrorRatePercent] float NULL,
        [ActiveSessions] int NULL,
        [DatabaseHealthy] bit NULL,
        [ReportedVersion] nvarchar(400) NULL,
        [AwaitingManualRestore] bit NOT NULL,
        CONSTRAINT [PK_ServerHealthCurrent] PRIMARY KEY ([ServerId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ServerHealthHistory] (
        [Id] uniqueidentifier NOT NULL,
        [ServerId] uniqueidentifier NOT NULL,
        [FromState] nvarchar(40) NOT NULL,
        [ToState] nvarchar(40) NOT NULL,
        [At] datetimeoffset NOT NULL,
        [Reason] nvarchar(400) NULL,
        [Source] nvarchar(40) NOT NULL,
        [ReportedBy] nvarchar(400) NULL,
        CONSTRAINT [PK_ServerHealthHistory] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ServerMetrics] (
        [Id] uniqueidentifier NOT NULL,
        [ServerId] uniqueidentifier NOT NULL,
        [CapturedAt] datetimeoffset NOT NULL,
        [CpuPercent] float NULL,
        [MemoryPercent] float NULL,
        [DiskPercent] float NULL,
        [ActiveConnections] int NULL,
        [ActiveRequests] int NULL,
        [QueueLength] int NULL,
        [ErrorRatePercent] float NULL,
        [ResponseTimeMs] float NULL,
        [ActiveSessions] int NULL,
        [DatabaseHealthy] bit NULL,
        CONSTRAINT [PK_ServerMetrics] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [Servers] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Hostname] nvarchar(400) NOT NULL,
        [IpAddress] nvarchar(400) NULL,
        [BaseUrl] nvarchar(1000) NOT NULL,
        [PublicUrl] nvarchar(1000) NULL,
        [HealthCheckUrl] nvarchar(1000) NULL,
        [Environment] nvarchar(40) NOT NULL,
        [Region] nvarchar(400) NULL,
        [ServerType] nvarchar(40) NOT NULL,
        [CpuCores] int NULL,
        [RamGb] int NULL,
        [MaxCapacity] int NOT NULL,
        [ApplicationVersion] nvarchar(400) NULL,
        [AdminState] nvarchar(40) NOT NULL,
        [Priority] int NOT NULL,
        [Weight] int NOT NULL,
        [RequireManualRestore] bit NOT NULL,
        [DrainStartedAt] datetimeoffset NULL,
        [DrainTimeoutMinutes] int NOT NULL,
        [ClientCertificateThumbprint] nvarchar(400) NULL,
        [BackendCertificateThumbprint] nvarchar(400) NULL,
        [Notes] nvarchar(4000) NULL,
        [RetiredAt] datetimeoffset NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ModifiedAt] datetimeoffset NOT NULL,
        [ModifiedBy] nvarchar(400) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Servers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ServiceLeases] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Holder] nvarchar(400) NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ServiceLeases] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [SystemSettings] (
        [Id] uniqueidentifier NOT NULL,
        [Key] nvarchar(400) NOT NULL,
        [Value] nvarchar(4000) NOT NULL,
        [Description] nvarchar(400) NULL,
        [ModifiedAt] datetimeoffset NOT NULL,
        [ModifiedBy] nvarchar(400) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_SystemSettings] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] uniqueidentifier NOT NULL,
        [DisplayName] nvarchar(400) NOT NULL,
        [Email] nvarchar(400) NULL,
        [Status] nvarchar(40) NOT NULL,
        [PasswordHash] nvarchar(512) NULL,
        [MfaEnabled] bit NOT NULL,
        [MfaSecretProtected] nvarchar(1024) NULL,
        [FailedLoginCount] int NOT NULL,
        [LockoutEndAt] datetimeoffset NULL,
        [LastLoginAt] datetimeoffset NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ModifiedAt] datetimeoffset NOT NULL,
        [ModifiedBy] nvarchar(400) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ApplicationEnvironments] (
        [Id] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [Environment] nvarchar(40) NOT NULL,
        [PublicHost] nvarchar(400) NOT NULL,
        [PathPrefix] nvarchar(400) NOT NULL,
        [RoutingMode] nvarchar(40) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [RequestTimeoutSeconds] int NOT NULL,
        [MaxRequestBodyBytes] bigint NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ApplicationEnvironments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ApplicationEnvironments_Applications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [AdministratorRoles] (
        [Id] uniqueidentifier NOT NULL,
        [AdministratorId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_AdministratorRoles] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AdministratorRoles_Administrators_AdministratorId] FOREIGN KEY ([AdministratorId]) REFERENCES [Administrators] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AdministratorRoles_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [RolePermissions] (
        [Id] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        [PermissionId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_RolePermissions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RolePermissions_Permissions_PermissionId] FOREIGN KEY ([PermissionId]) REFERENCES [Permissions] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_RolePermissions_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [Companies] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [NormalizedCode] nvarchar(64) NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [ServicePlan] nvarchar(400) NULL,
        [RoutingMode] nvarchar(40) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [DefaultEnvironment] nvarchar(40) NOT NULL,
        [MaxConcurrentUsers] int NULL,
        [AuthenticationMode] nvarchar(40) NOT NULL,
        [RoutingPolicyId] uniqueidentifier NULL,
        [Notes] nvarchar(4000) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ModifiedAt] datetimeoffset NOT NULL,
        [ModifiedBy] nvarchar(400) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Companies] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Companies_RoutingPolicies_RoutingPolicyId] FOREIGN KEY ([RoutingPolicyId]) REFERENCES [RoutingPolicies] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [MaintenanceWindows] (
        [Id] uniqueidentifier NOT NULL,
        [ServerId] uniqueidentifier NOT NULL,
        [StartsAt] datetimeoffset NOT NULL,
        [EndsAt] datetimeoffset NOT NULL,
        [DrainTimeoutMinutes] int NOT NULL,
        [AutoRestore] bit NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [Reason] nvarchar(400) NULL,
        [CreatedBy] nvarchar(400) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_MaintenanceWindows] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MaintenanceWindows_Servers_ServerId] FOREIGN KEY ([ServerId]) REFERENCES [Servers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ServerApplications] (
        [Id] uniqueidentifier NOT NULL,
        [ServerId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ServerApplications] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ServerApplications_Applications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_ServerApplications_Servers_ServerId] FOREIGN KEY ([ServerId]) REFERENCES [Servers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [CapacityReservations] (
        [Id] uniqueidentifier NOT NULL,
        [ServerId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ReservedSessions] int NOT NULL,
        CONSTRAINT [PK_CapacityReservations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CapacityReservations_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_CapacityReservations_Servers_ServerId] FOREIGN KEY ([ServerId]) REFERENCES [Servers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [CompanyAliases] (
        [Id] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [Alias] nvarchar(400) NOT NULL,
        [NormalizedAlias] nvarchar(400) NOT NULL,
        CONSTRAINT [PK_CompanyAliases] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CompanyAliases_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [CompanyServerAuthorizations] (
        [Id] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ServerId] uniqueidentifier NOT NULL,
        [Notes] nvarchar(400) NULL,
        [GrantedBy] nvarchar(400) NULL,
        [GrantedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_CompanyServerAuthorizations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CompanyServerAuthorizations_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_CompanyServerAuthorizations_Servers_ServerId] FOREIGN KEY ([ServerId]) REFERENCES [Servers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [UserCompanyMemberships] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [Username] nvarchar(400) NOT NULL,
        [NormalizedUsername] nvarchar(400) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [ExternalSubject] nvarchar(400) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_UserCompanyMemberships] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserCompanyMemberships_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_UserCompanyMemberships_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [CompanyServerAssignments] (
        [Id] uniqueidentifier NOT NULL,
        [PrimaryServerId] uniqueidentifier NOT NULL,
        [SecondaryServerId] uniqueidentifier NULL,
        [ClusterPoolId] uniqueidentifier NULL,
        [ClusterMode] nvarchar(40) NOT NULL,
        [FailoverPolicyId] uniqueidentifier NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Environment] nvarchar(40) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [EffectiveFrom] datetimeoffset NOT NULL,
        [EffectiveTo] datetimeoffset NULL,
        [Reason] nvarchar(1000) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [CreatedBy] nvarchar(400) NULL,
        [SupersededById] uniqueidentifier NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_CompanyServerAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CompanyServerAssignments_Applications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CompanyServerAssignments_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CompanyServerAssignments_Servers_PrimaryServerId] FOREIGN KEY ([PrimaryServerId]) REFERENCES [Servers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CompanyServerAssignments_Servers_SecondaryServerId] FOREIGN KEY ([SecondaryServerId]) REFERENCES [Servers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [FailoverPolicies] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(400) NULL,
        [FailoverEnabled] bit NOT NULL,
        [FailoverPoolId] uniqueidentifier NULL,
        [FailbackPolicy] nvarchar(40) NOT NULL,
        [MaxFailoverAttempts] int NOT NULL,
        [FailoverPriority] int NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_FailoverPolicies] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ServerPools] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(400) NULL,
        [Environment] nvarchar(40) NOT NULL,
        [Region] nvarchar(400) NULL,
        [Algorithm] nvarchar(40) NOT NULL,
        [Affinity] nvarchar(40) NOT NULL,
        [FailoverPolicyId] uniqueidentifier NULL,
        [MaxCapacity] int NULL,
        [EligiblePlans] nvarchar(400) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ModifiedAt] datetimeoffset NOT NULL,
        [ModifiedBy] nvarchar(400) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ServerPools] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ServerPools_FailoverPolicies_FailoverPolicyId] FOREIGN KEY ([FailoverPolicyId]) REFERENCES [FailoverPolicies] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [UserServerAssignments] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [PrimaryServerId] uniqueidentifier NOT NULL,
        [SecondaryServerId] uniqueidentifier NULL,
        [FailoverPolicyId] uniqueidentifier NULL,
        [AllowCompanyRouteFallback] bit NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Environment] nvarchar(40) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [EffectiveFrom] datetimeoffset NOT NULL,
        [EffectiveTo] datetimeoffset NULL,
        [Reason] nvarchar(1000) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [CreatedBy] nvarchar(400) NULL,
        [SupersededById] uniqueidentifier NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_UserServerAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserServerAssignments_Applications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_UserServerAssignments_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_UserServerAssignments_FailoverPolicies_FailoverPolicyId] FOREIGN KEY ([FailoverPolicyId]) REFERENCES [FailoverPolicies] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_UserServerAssignments_Servers_PrimaryServerId] FOREIGN KEY ([PrimaryServerId]) REFERENCES [Servers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_UserServerAssignments_Servers_SecondaryServerId] FOREIGN KEY ([SecondaryServerId]) REFERENCES [Servers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_UserServerAssignments_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ServerPoolApplications] (
        [Id] uniqueidentifier NOT NULL,
        [PoolId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ServerPoolApplications] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ServerPoolApplications_Applications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_ServerPoolApplications_ServerPools_PoolId] FOREIGN KEY ([PoolId]) REFERENCES [ServerPools] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [ServerPoolMembers] (
        [Id] uniqueidentifier NOT NULL,
        [PoolId] uniqueidentifier NOT NULL,
        [ServerId] uniqueidentifier NOT NULL,
        [WeightOverride] int NULL,
        [Priority] int NOT NULL,
        [AcceptingNewSessions] bit NOT NULL,
        [AddedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_ServerPoolMembers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ServerPoolMembers_ServerPools_PoolId] FOREIGN KEY ([PoolId]) REFERENCES [ServerPools] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_ServerPoolMembers_Servers_ServerId] FOREIGN KEY ([ServerId]) REFERENCES [Servers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE TABLE [SharedPoolAssignments] (
        [Id] uniqueidentifier NOT NULL,
        [PoolId] uniqueidentifier NOT NULL,
        [Priority] int NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Environment] nvarchar(40) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [EffectiveFrom] datetimeoffset NOT NULL,
        [EffectiveTo] datetimeoffset NULL,
        [Reason] nvarchar(1000) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [CreatedBy] nvarchar(400) NULL,
        [SupersededById] uniqueidentifier NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_SharedPoolAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SharedPoolAssignments_Applications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SharedPoolAssignments_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SharedPoolAssignments_ServerPools_PoolId] FOREIGN KEY ([PoolId]) REFERENCES [ServerPools] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ActiveSessions_CompanyId_EndedAt] ON [ActiveSessions] ([CompanyId], [EndedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ActiveSessions_RevokedAt] ON [ActiveSessions] ([RevokedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ActiveSessions_ServerId_EndedAt] ON [ActiveSessions] ([ServerId], [EndedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ActiveSessions_SessionKey_ApplicationId] ON [ActiveSessions] ([SessionKey], [ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AdministratorRoles_AdministratorId_RoleId] ON [AdministratorRoles] ([AdministratorId], [RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AdministratorRoles_RoleId] ON [AdministratorRoles] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Administrators_NormalizedUsername] ON [Administrators] ([NormalizedUsername]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Alerts_DedupKey_Status] ON [Alerts] ([DedupKey], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Alerts_Status_LastRaisedAt] ON [Alerts] ([Status], [LastRaisedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ApplicationEnvironments_ApplicationId_Environment] ON [ApplicationEnvironments] ([ApplicationId], [Environment]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ApplicationEnvironments_PublicHost_PathPrefix] ON [ApplicationEnvironments] ([PublicHost], [PathPrefix]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Applications_Code] ON [Applications] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_At] ON [AuditLogs] ([At]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_CompanyId_At] ON [AuditLogs] ([CompanyId], [At]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_EntityType_EntityId] ON [AuditLogs] ([EntityType], [EntityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_EventType_At] ON [AuditLogs] ([EventType], [At]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CapacityReservations_CompanyId] ON [CapacityReservations] ([CompanyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CapacityReservations_ServerId_CompanyId] ON [CapacityReservations] ([ServerId], [CompanyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Companies_Name] ON [Companies] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Companies_NormalizedCode] ON [Companies] ([NormalizedCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Companies_RoutingPolicyId] ON [Companies] ([RoutingPolicyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CompanyAliases_CompanyId] ON [CompanyAliases] ([CompanyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CompanyAliases_NormalizedAlias] ON [CompanyAliases] ([NormalizedAlias]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CompanyServerAssignments_ApplicationId] ON [CompanyServerAssignments] ([ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CompanyServerAssignments_ClusterPoolId] ON [CompanyServerAssignments] ([ClusterPoolId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_CompanyServerAssignments_CompanyId_Environment_ApplicationId_EffectiveFrom] ON [CompanyServerAssignments] ([CompanyId], [Environment], [ApplicationId], [EffectiveFrom]) WHERE [ApplicationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CompanyServerAssignments_CompanyId_IsEnabled_EffectiveTo] ON [CompanyServerAssignments] ([CompanyId], [IsEnabled], [EffectiveTo]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CompanyServerAssignments_FailoverPolicyId] ON [CompanyServerAssignments] ([FailoverPolicyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CompanyServerAssignments_PrimaryServerId] ON [CompanyServerAssignments] ([PrimaryServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CompanyServerAssignments_SecondaryServerId] ON [CompanyServerAssignments] ([SecondaryServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CompanyServerAuthorizations_CompanyId_ServerId] ON [CompanyServerAuthorizations] ([CompanyId], [ServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CompanyServerAuthorizations_ServerId] ON [CompanyServerAuthorizations] ([ServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ConfigurationVersions_ChangedAt] ON [ConfigurationVersions] ([ChangedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_FailoverPolicies_FailoverPoolId] ON [FailoverPolicies] ([FailoverPoolId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FailoverPolicies_Name] ON [FailoverPolicies] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_GatewayInstances_InstanceName] ON [GatewayInstances] ([InstanceName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_GatewayTrafficSamples_At_GatewayInstance] ON [GatewayTrafficSamples] ([At], [GatewayInstance]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaintenanceWindows_ServerId_StartsAt] ON [MaintenanceWindows] ([ServerId], [StartsAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaintenanceWindows_Status_StartsAt] ON [MaintenanceWindows] ([Status], [StartsAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationChannels_Name] ON [NotificationChannels] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Permissions_Code] ON [Permissions] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RolePermissions_PermissionId] ON [RolePermissions] ([PermissionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RolePermissions_RoleId_PermissionId] ON [RolePermissions] ([RoleId], [PermissionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Roles_Name] ON [Roles] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RoutingDecisionLogs_At] ON [RoutingDecisionLogs] ([At]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RoutingDecisionLogs_CompanyId_At] ON [RoutingDecisionLogs] ([CompanyId], [At]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RoutingDecisionLogs_Outcome_At] ON [RoutingDecisionLogs] ([Outcome], [At]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RoutingDecisionLogs_ServerId_At] ON [RoutingDecisionLogs] ([ServerId], [At]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RoutingPolicies_Name] ON [RoutingPolicies] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ServerApplications_ApplicationId] ON [ServerApplications] ([ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ServerApplications_ServerId_ApplicationId] ON [ServerApplications] ([ServerId], [ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ServerHealthHistory_ServerId_At] ON [ServerHealthHistory] ([ServerId], [At]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ServerMetrics_CapturedAt] ON [ServerMetrics] ([CapturedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ServerMetrics_ServerId_CapturedAt] ON [ServerMetrics] ([ServerId], [CapturedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ServerPoolApplications_ApplicationId] ON [ServerPoolApplications] ([ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ServerPoolApplications_PoolId_ApplicationId] ON [ServerPoolApplications] ([PoolId], [ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ServerPoolMembers_PoolId_ServerId] ON [ServerPoolMembers] ([PoolId], [ServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ServerPoolMembers_ServerId] ON [ServerPoolMembers] ([ServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ServerPools_FailoverPolicyId] ON [ServerPools] ([FailoverPolicyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ServerPools_Name] ON [ServerPools] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Servers_Environment_AdminState] ON [Servers] ([Environment], [AdminState]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Servers_Name] ON [Servers] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ServiceLeases_Name] ON [ServiceLeases] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SharedPoolAssignments_ApplicationId] ON [SharedPoolAssignments] ([ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SharedPoolAssignments_CompanyId_IsEnabled_EffectiveTo] ON [SharedPoolAssignments] ([CompanyId], [IsEnabled], [EffectiveTo]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_SharedPoolAssignments_CompanyId_PoolId_Environment_ApplicationId_EffectiveFrom] ON [SharedPoolAssignments] ([CompanyId], [PoolId], [Environment], [ApplicationId], [EffectiveFrom]) WHERE [ApplicationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SharedPoolAssignments_PoolId] ON [SharedPoolAssignments] ([PoolId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SystemSettings_Key] ON [SystemSettings] ([Key]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_UserCompanyMemberships_CompanyId_ExternalSubject] ON [UserCompanyMemberships] ([CompanyId], [ExternalSubject]) WHERE [ExternalSubject] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserCompanyMemberships_CompanyId_NormalizedUsername] ON [UserCompanyMemberships] ([CompanyId], [NormalizedUsername]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserCompanyMemberships_CompanyId_UserId] ON [UserCompanyMemberships] ([CompanyId], [UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserCompanyMemberships_UserId] ON [UserCompanyMemberships] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Users_Email] ON [Users] ([Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserServerAssignments_ApplicationId] ON [UserServerAssignments] ([ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserServerAssignments_CompanyId_IsEnabled_EffectiveTo] ON [UserServerAssignments] ([CompanyId], [IsEnabled], [EffectiveTo]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_UserServerAssignments_CompanyId_UserId_Environment_ApplicationId_EffectiveFrom] ON [UserServerAssignments] ([CompanyId], [UserId], [Environment], [ApplicationId], [EffectiveFrom]) WHERE [ApplicationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserServerAssignments_FailoverPolicyId] ON [UserServerAssignments] ([FailoverPolicyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserServerAssignments_PrimaryServerId] ON [UserServerAssignments] ([PrimaryServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserServerAssignments_SecondaryServerId] ON [UserServerAssignments] ([SecondaryServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserServerAssignments_UserId] ON [UserServerAssignments] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    ALTER TABLE [CompanyServerAssignments] ADD CONSTRAINT [FK_CompanyServerAssignments_FailoverPolicies_FailoverPolicyId] FOREIGN KEY ([FailoverPolicyId]) REFERENCES [FailoverPolicies] ([Id]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    ALTER TABLE [CompanyServerAssignments] ADD CONSTRAINT [FK_CompanyServerAssignments_ServerPools_ClusterPoolId] FOREIGN KEY ([ClusterPoolId]) REFERENCES [ServerPools] ([Id]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    ALTER TABLE [FailoverPolicies] ADD CONSTRAINT [FK_FailoverPolicies_ServerPools_FailoverPoolId] FOREIGN KEY ([FailoverPoolId]) REFERENCES [ServerPools] ([Id]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261027172028_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261027172028_InitialCreate', N'10.0.14');
END;

COMMIT;
GO

