CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

BEGIN TRANSACTION;
CREATE TABLE "ActiveSessions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ActiveSessions" PRIMARY KEY,
    "SessionKey" TEXT NOT NULL,
    "CompanyId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "ApplicationId" TEXT NOT NULL,
    "ServerId" TEXT NULL,
    "GatewayInstance" TEXT NULL,
    "StartedAt" INTEGER NOT NULL,
    "LastSeenAt" INTEGER NOT NULL,
    "EndedAt" INTEGER NULL,
    "EndReason" TEXT NULL,
    "RevokedAt" INTEGER NULL,
    "RevokedBy" TEXT NULL,
    "ClientAddress" TEXT NULL,
    "UserAgent" TEXT NULL
);

CREATE TABLE "Administrators" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Administrators" PRIMARY KEY,
    "Username" TEXT NOT NULL,
    "NormalizedUsername" TEXT NOT NULL,
    "DisplayName" TEXT NOT NULL,
    "Email" TEXT NULL,
    "PasswordHash" TEXT NULL,
    "MfaEnabled" INTEGER NOT NULL,
    "MfaSecretProtected" TEXT NULL,
    "Status" TEXT NOT NULL,
    "FailedLoginCount" INTEGER NOT NULL,
    "LockoutEndAt" INTEGER NULL,
    "LastLoginAt" INTEGER NULL,
    "CreatedAt" INTEGER NOT NULL,
    "ModifiedAt" INTEGER NOT NULL,
    "ModifiedBy" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL
);

CREATE TABLE "Alerts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Alerts" PRIMARY KEY,
    "Type" TEXT NOT NULL,
    "Severity" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "DedupKey" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "Message" TEXT NULL,
    "ServerId" TEXT NULL,
    "CompanyId" TEXT NULL,
    "FirstRaisedAt" INTEGER NOT NULL,
    "LastRaisedAt" INTEGER NOT NULL,
    "Occurrences" INTEGER NOT NULL,
    "AcknowledgedAt" INTEGER NULL,
    "AcknowledgedBy" TEXT NULL,
    "ResolvedAt" INTEGER NULL,
    "ResolvedBy" TEXT NULL,
    "ResolutionNote" TEXT NULL
);

CREATE TABLE "Applications" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Applications" PRIMARY KEY,
    "Code" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "IsEnabled" INTEGER NOT NULL,
    "MinimumServerVersion" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL
);

CREATE TABLE "AuditLogs" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY,
    "At" INTEGER NOT NULL,
    "Actor" TEXT NOT NULL,
    "ActorType" TEXT NOT NULL,
    "EventType" TEXT NOT NULL,
    "EntityType" TEXT NULL,
    "EntityId" TEXT NULL,
    "CompanyId" TEXT NULL,
    "UserId" TEXT NULL,
    "ServerId" TEXT NULL,
    "PreviousValue" TEXT NULL,
    "NewValue" TEXT NULL,
    "Reason" TEXT NULL,
    "CorrelationId" TEXT NULL,
    "SourceIp" TEXT NULL,
    "Severity" TEXT NOT NULL
);

CREATE TABLE "ConfigurationVersions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ConfigurationVersions" PRIMARY KEY,
    "Version" INTEGER NOT NULL,
    "ChangedAt" INTEGER NOT NULL,
    "ChangedBy" TEXT NULL,
    "Summary" TEXT NULL
);

CREATE TABLE "DataProtectionKeys" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_DataProtectionKeys" PRIMARY KEY AUTOINCREMENT,
    "FriendlyName" TEXT NULL,
    "Xml" TEXT NULL
);

CREATE TABLE "GatewayInstances" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_GatewayInstances" PRIMARY KEY,
    "InstanceName" TEXT NOT NULL,
    "Host" TEXT NULL,
    "Version" TEXT NULL,
    "StartedAt" INTEGER NOT NULL,
    "LastHeartbeatAt" INTEGER NOT NULL,
    "ConfigVersion" INTEGER NOT NULL,
    "RequestsPerSecond" REAL NOT NULL,
    "AverageLatencyMs" REAL NOT NULL,
    "AverageDecisionMicroseconds" REAL NOT NULL,
    "FailedRoutingLastInterval" INTEGER NOT NULL,
    "FailoversLastInterval" INTEGER NOT NULL,
    "ActiveRequests" INTEGER NOT NULL,
    "RedisAvailable" INTEGER NOT NULL,
    "DatabaseAvailable" INTEGER NOT NULL,
    "ConfigurationStale" INTEGER NOT NULL
);

CREATE TABLE "GatewayTrafficSamples" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_GatewayTrafficSamples" PRIMARY KEY,
    "GatewayInstance" TEXT NOT NULL,
    "At" INTEGER NOT NULL,
    "IntervalSeconds" INTEGER NOT NULL,
    "Requests" INTEGER NOT NULL,
    "AverageLatencyMs" REAL NOT NULL,
    "AverageDecisionMicroseconds" REAL NOT NULL,
    "FailedRouting" INTEGER NOT NULL,
    "Failovers" INTEGER NOT NULL
);

CREATE TABLE "NotificationChannels" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_NotificationChannels" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "Target" TEXT NOT NULL,
    "MinimumSeverity" TEXT NOT NULL,
    "IsEnabled" INTEGER NOT NULL,
    "ConcurrencyStamp" TEXT NOT NULL
);

CREATE TABLE "Permissions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Permissions" PRIMARY KEY,
    "Code" TEXT NOT NULL,
    "Description" TEXT NULL
);

CREATE TABLE "Roles" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Roles" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "IsSystem" INTEGER NOT NULL
);

CREATE TABLE "RoutingDecisionLogs" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_RoutingDecisionLogs" PRIMARY KEY,
    "At" INTEGER NOT NULL,
    "CorrelationId" TEXT NULL,
    "GatewayInstance" TEXT NULL,
    "CompanyId" TEXT NULL,
    "UserId" TEXT NULL,
    "ApplicationId" TEXT NULL,
    "Environment" TEXT NULL,
    "Outcome" TEXT NOT NULL,
    "ServerId" TEXT NULL,
    "Source" TEXT NOT NULL,
    "PoolId" TEXT NULL,
    "IsFailover" INTEGER NOT NULL,
    "IsFailback" INTEGER NOT NULL,
    "AffinityApplied" INTEGER NOT NULL,
    "Reason" TEXT NULL,
    "ConfigVersion" INTEGER NOT NULL,
    "DecisionMicroseconds" REAL NOT NULL,
    "SessionKey" TEXT NULL,
    "EventKind" TEXT NULL
);

CREATE TABLE "RoutingPolicies" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_RoutingPolicies" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "IsDefault" INTEGER NOT NULL,
    "AllowDegradedServers" INTEGER NOT NULL,
    "DefaultAffinity" TEXT NOT NULL,
    "CpuWeight" REAL NOT NULL,
    "MemoryWeight" REAL NOT NULL,
    "ActiveRequestsWeight" REAL NOT NULL,
    "ResponseTimeWeight" REAL NOT NULL,
    "ErrorRateWeight" REAL NOT NULL,
    "ResponseTimeCeilingMs" REAL NOT NULL,
    "SessionIdleTimeoutMinutes" INTEGER NOT NULL,
    "SessionAbsoluteTimeoutHours" INTEGER NOT NULL,
    "ConcurrencyStamp" TEXT NOT NULL
);

CREATE TABLE "ServerHealthCurrent" (
    "ServerId" TEXT NOT NULL CONSTRAINT "PK_ServerHealthCurrent" PRIMARY KEY,
    "State" TEXT NOT NULL,
    "StateChangedAt" INTEGER NULL,
    "LastCheckAt" INTEGER NULL,
    "LastHeartbeatAt" INTEGER NULL,
    "LastReason" TEXT NULL,
    "ConsecutiveFailures" INTEGER NOT NULL,
    "ResponseTimeMs" REAL NULL,
    "CpuPercent" REAL NULL,
    "MemoryPercent" REAL NULL,
    "DiskPercent" REAL NULL,
    "ActiveConnections" INTEGER NULL,
    "ActiveRequests" INTEGER NULL,
    "QueueLength" INTEGER NULL,
    "ErrorRatePercent" REAL NULL,
    "ActiveSessions" INTEGER NULL,
    "DatabaseHealthy" INTEGER NULL,
    "ReportedVersion" TEXT NULL,
    "AwaitingManualRestore" INTEGER NOT NULL
);

CREATE TABLE "ServerHealthHistory" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ServerHealthHistory" PRIMARY KEY,
    "ServerId" TEXT NOT NULL,
    "FromState" TEXT NOT NULL,
    "ToState" TEXT NOT NULL,
    "At" INTEGER NOT NULL,
    "Reason" TEXT NULL,
    "Source" TEXT NOT NULL,
    "ReportedBy" TEXT NULL
);

CREATE TABLE "ServerMetrics" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ServerMetrics" PRIMARY KEY,
    "ServerId" TEXT NOT NULL,
    "CapturedAt" INTEGER NOT NULL,
    "CpuPercent" REAL NULL,
    "MemoryPercent" REAL NULL,
    "DiskPercent" REAL NULL,
    "ActiveConnections" INTEGER NULL,
    "ActiveRequests" INTEGER NULL,
    "QueueLength" INTEGER NULL,
    "ErrorRatePercent" REAL NULL,
    "ResponseTimeMs" REAL NULL,
    "ActiveSessions" INTEGER NULL,
    "DatabaseHealthy" INTEGER NULL
);

CREATE TABLE "Servers" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Servers" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Hostname" TEXT NOT NULL,
    "IpAddress" TEXT NULL,
    "BaseUrl" TEXT NOT NULL,
    "PublicUrl" TEXT NULL,
    "HealthCheckUrl" TEXT NULL,
    "Environment" TEXT NOT NULL,
    "Region" TEXT NULL,
    "ServerType" TEXT NOT NULL,
    "CpuCores" INTEGER NULL,
    "RamGb" INTEGER NULL,
    "MaxCapacity" INTEGER NOT NULL,
    "ApplicationVersion" TEXT NULL,
    "AdminState" TEXT NOT NULL,
    "Priority" INTEGER NOT NULL,
    "Weight" INTEGER NOT NULL,
    "RequireManualRestore" INTEGER NOT NULL,
    "DrainStartedAt" INTEGER NULL,
    "DrainTimeoutMinutes" INTEGER NOT NULL,
    "ClientCertificateThumbprint" TEXT NULL,
    "BackendCertificateThumbprint" TEXT NULL,
    "Notes" TEXT NULL,
    "RetiredAt" INTEGER NULL,
    "CreatedAt" INTEGER NOT NULL,
    "ModifiedAt" INTEGER NOT NULL,
    "ModifiedBy" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL
);

CREATE TABLE "ServiceLeases" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ServiceLeases" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Holder" TEXT NOT NULL,
    "ExpiresAt" INTEGER NOT NULL,
    "ConcurrencyStamp" TEXT NOT NULL
);

CREATE TABLE "SystemSettings" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_SystemSettings" PRIMARY KEY,
    "Key" TEXT NOT NULL,
    "Value" TEXT NOT NULL,
    "Description" TEXT NULL,
    "ModifiedAt" INTEGER NOT NULL,
    "ModifiedBy" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL
);

CREATE TABLE "Users" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
    "DisplayName" TEXT NOT NULL,
    "Email" TEXT NULL,
    "Status" TEXT NOT NULL,
    "PasswordHash" TEXT NULL,
    "MfaEnabled" INTEGER NOT NULL,
    "MfaSecretProtected" TEXT NULL,
    "FailedLoginCount" INTEGER NOT NULL,
    "LockoutEndAt" INTEGER NULL,
    "LastLoginAt" INTEGER NULL,
    "CreatedAt" INTEGER NOT NULL,
    "ModifiedAt" INTEGER NOT NULL,
    "ModifiedBy" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL
);

CREATE TABLE "ApplicationEnvironments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ApplicationEnvironments" PRIMARY KEY,
    "ApplicationId" TEXT NOT NULL,
    "Environment" TEXT NOT NULL,
    "PublicHost" TEXT NOT NULL,
    "PathPrefix" TEXT NOT NULL,
    "RoutingMode" TEXT NOT NULL,
    "IsEnabled" INTEGER NOT NULL,
    "RequestTimeoutSeconds" INTEGER NOT NULL,
    "MaxRequestBodyBytes" INTEGER NULL,
    "ConcurrencyStamp" TEXT NOT NULL,
    CONSTRAINT "FK_ApplicationEnvironments_Applications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "Applications" ("Id") ON DELETE CASCADE
);

CREATE TABLE "AdministratorRoles" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_AdministratorRoles" PRIMARY KEY,
    "AdministratorId" TEXT NOT NULL,
    "RoleId" TEXT NOT NULL,
    CONSTRAINT "FK_AdministratorRoles_Administrators_AdministratorId" FOREIGN KEY ("AdministratorId") REFERENCES "Administrators" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AdministratorRoles_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE
);

CREATE TABLE "RolePermissions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_RolePermissions" PRIMARY KEY,
    "RoleId" TEXT NOT NULL,
    "PermissionId" TEXT NOT NULL,
    CONSTRAINT "FK_RolePermissions_Permissions_PermissionId" FOREIGN KEY ("PermissionId") REFERENCES "Permissions" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_RolePermissions_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE
);

CREATE TABLE "Companies" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Companies" PRIMARY KEY,
    "Code" TEXT NOT NULL,
    "NormalizedCode" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "ServicePlan" TEXT NULL,
    "RoutingMode" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "DefaultEnvironment" TEXT NOT NULL,
    "MaxConcurrentUsers" INTEGER NULL,
    "AuthenticationMode" TEXT NOT NULL,
    "RoutingPolicyId" TEXT NULL,
    "Notes" TEXT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "ModifiedAt" INTEGER NOT NULL,
    "ModifiedBy" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL,
    CONSTRAINT "FK_Companies_RoutingPolicies_RoutingPolicyId" FOREIGN KEY ("RoutingPolicyId") REFERENCES "RoutingPolicies" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "MaintenanceWindows" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_MaintenanceWindows" PRIMARY KEY,
    "ServerId" TEXT NOT NULL,
    "StartsAt" INTEGER NOT NULL,
    "EndsAt" INTEGER NOT NULL,
    "DrainTimeoutMinutes" INTEGER NOT NULL,
    "AutoRestore" INTEGER NOT NULL,
    "Status" TEXT NOT NULL,
    "Reason" TEXT NULL,
    "CreatedBy" TEXT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "ConcurrencyStamp" TEXT NOT NULL,
    CONSTRAINT "FK_MaintenanceWindows_Servers_ServerId" FOREIGN KEY ("ServerId") REFERENCES "Servers" ("Id") ON DELETE CASCADE
);

CREATE TABLE "ServerApplications" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ServerApplications" PRIMARY KEY,
    "ServerId" TEXT NOT NULL,
    "ApplicationId" TEXT NOT NULL,
    CONSTRAINT "FK_ServerApplications_Applications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "Applications" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ServerApplications_Servers_ServerId" FOREIGN KEY ("ServerId") REFERENCES "Servers" ("Id") ON DELETE CASCADE
);

CREATE TABLE "CapacityReservations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CapacityReservations" PRIMARY KEY,
    "ServerId" TEXT NOT NULL,
    "CompanyId" TEXT NOT NULL,
    "ReservedSessions" INTEGER NOT NULL,
    CONSTRAINT "FK_CapacityReservations_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_CapacityReservations_Servers_ServerId" FOREIGN KEY ("ServerId") REFERENCES "Servers" ("Id") ON DELETE CASCADE
);

CREATE TABLE "CompanyAliases" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CompanyAliases" PRIMARY KEY,
    "CompanyId" TEXT NOT NULL,
    "Alias" TEXT NOT NULL,
    "NormalizedAlias" TEXT NOT NULL,
    CONSTRAINT "FK_CompanyAliases_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE
);

CREATE TABLE "CompanyServerAuthorizations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CompanyServerAuthorizations" PRIMARY KEY,
    "CompanyId" TEXT NOT NULL,
    "ServerId" TEXT NOT NULL,
    "Notes" TEXT NULL,
    "GrantedBy" TEXT NULL,
    "GrantedAt" INTEGER NOT NULL,
    CONSTRAINT "FK_CompanyServerAuthorizations_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_CompanyServerAuthorizations_Servers_ServerId" FOREIGN KEY ("ServerId") REFERENCES "Servers" ("Id") ON DELETE CASCADE
);

CREATE TABLE "UserCompanyMemberships" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_UserCompanyMemberships" PRIMARY KEY,
    "UserId" TEXT NOT NULL,
    "CompanyId" TEXT NOT NULL,
    "Username" TEXT NOT NULL,
    "NormalizedUsername" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "ExternalSubject" TEXT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "ConcurrencyStamp" TEXT NOT NULL,
    CONSTRAINT "FK_UserCompanyMemberships_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserCompanyMemberships_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "CompanyServerAssignments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CompanyServerAssignments" PRIMARY KEY,
    "PrimaryServerId" TEXT NOT NULL,
    "SecondaryServerId" TEXT NULL,
    "ClusterPoolId" TEXT NULL,
    "ClusterMode" TEXT NOT NULL,
    "FailoverPolicyId" TEXT NULL,
    "CompanyId" TEXT NOT NULL,
    "ApplicationId" TEXT NULL,
    "Environment" TEXT NOT NULL,
    "IsEnabled" INTEGER NOT NULL,
    "EffectiveFrom" INTEGER NOT NULL,
    "EffectiveTo" INTEGER NULL,
    "Reason" TEXT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "CreatedBy" TEXT NULL,
    "SupersededById" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL,
    CONSTRAINT "FK_CompanyServerAssignments_Applications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "Applications" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_CompanyServerAssignments_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_CompanyServerAssignments_Servers_PrimaryServerId" FOREIGN KEY ("PrimaryServerId") REFERENCES "Servers" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_CompanyServerAssignments_Servers_SecondaryServerId" FOREIGN KEY ("SecondaryServerId") REFERENCES "Servers" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_CompanyServerAssignments_FailoverPolicies_FailoverPolicyId" FOREIGN KEY ("FailoverPolicyId") REFERENCES "FailoverPolicies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_CompanyServerAssignments_ServerPools_ClusterPoolId" FOREIGN KEY ("ClusterPoolId") REFERENCES "ServerPools" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "FailoverPolicies" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_FailoverPolicies" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "FailoverEnabled" INTEGER NOT NULL,
    "FailoverPoolId" TEXT NULL,
    "FailbackPolicy" TEXT NOT NULL,
    "MaxFailoverAttempts" INTEGER NOT NULL,
    "FailoverPriority" INTEGER NOT NULL,
    "ConcurrencyStamp" TEXT NOT NULL,
    CONSTRAINT "FK_FailoverPolicies_ServerPools_FailoverPoolId" FOREIGN KEY ("FailoverPoolId") REFERENCES "ServerPools" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "ServerPools" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ServerPools" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "Environment" TEXT NOT NULL,
    "Region" TEXT NULL,
    "Algorithm" TEXT NOT NULL,
    "Affinity" TEXT NOT NULL,
    "FailoverPolicyId" TEXT NULL,
    "MaxCapacity" INTEGER NULL,
    "EligiblePlans" TEXT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "ModifiedAt" INTEGER NOT NULL,
    "ModifiedBy" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL,
    CONSTRAINT "FK_ServerPools_FailoverPolicies_FailoverPolicyId" FOREIGN KEY ("FailoverPolicyId") REFERENCES "FailoverPolicies" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "UserServerAssignments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_UserServerAssignments" PRIMARY KEY,
    "UserId" TEXT NOT NULL,
    "PrimaryServerId" TEXT NOT NULL,
    "SecondaryServerId" TEXT NULL,
    "FailoverPolicyId" TEXT NULL,
    "AllowCompanyRouteFallback" INTEGER NOT NULL,
    "CompanyId" TEXT NOT NULL,
    "ApplicationId" TEXT NULL,
    "Environment" TEXT NOT NULL,
    "IsEnabled" INTEGER NOT NULL,
    "EffectiveFrom" INTEGER NOT NULL,
    "EffectiveTo" INTEGER NULL,
    "Reason" TEXT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "CreatedBy" TEXT NULL,
    "SupersededById" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL,
    CONSTRAINT "FK_UserServerAssignments_Applications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "Applications" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserServerAssignments_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserServerAssignments_FailoverPolicies_FailoverPolicyId" FOREIGN KEY ("FailoverPolicyId") REFERENCES "FailoverPolicies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserServerAssignments_Servers_PrimaryServerId" FOREIGN KEY ("PrimaryServerId") REFERENCES "Servers" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserServerAssignments_Servers_SecondaryServerId" FOREIGN KEY ("SecondaryServerId") REFERENCES "Servers" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserServerAssignments_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "ServerPoolApplications" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ServerPoolApplications" PRIMARY KEY,
    "PoolId" TEXT NOT NULL,
    "ApplicationId" TEXT NOT NULL,
    CONSTRAINT "FK_ServerPoolApplications_Applications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "Applications" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ServerPoolApplications_ServerPools_PoolId" FOREIGN KEY ("PoolId") REFERENCES "ServerPools" ("Id") ON DELETE CASCADE
);

CREATE TABLE "ServerPoolMembers" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ServerPoolMembers" PRIMARY KEY,
    "PoolId" TEXT NOT NULL,
    "ServerId" TEXT NOT NULL,
    "WeightOverride" INTEGER NULL,
    "Priority" INTEGER NOT NULL,
    "AcceptingNewSessions" INTEGER NOT NULL,
    "AddedAt" INTEGER NOT NULL,
    CONSTRAINT "FK_ServerPoolMembers_ServerPools_PoolId" FOREIGN KEY ("PoolId") REFERENCES "ServerPools" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ServerPoolMembers_Servers_ServerId" FOREIGN KEY ("ServerId") REFERENCES "Servers" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "SharedPoolAssignments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_SharedPoolAssignments" PRIMARY KEY,
    "PoolId" TEXT NOT NULL,
    "Priority" INTEGER NOT NULL,
    "CompanyId" TEXT NOT NULL,
    "ApplicationId" TEXT NULL,
    "Environment" TEXT NOT NULL,
    "IsEnabled" INTEGER NOT NULL,
    "EffectiveFrom" INTEGER NOT NULL,
    "EffectiveTo" INTEGER NULL,
    "Reason" TEXT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "CreatedBy" TEXT NULL,
    "SupersededById" TEXT NULL,
    "ConcurrencyStamp" TEXT NOT NULL,
    CONSTRAINT "FK_SharedPoolAssignments_Applications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "Applications" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_SharedPoolAssignments_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_SharedPoolAssignments_ServerPools_PoolId" FOREIGN KEY ("PoolId") REFERENCES "ServerPools" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_ActiveSessions_CompanyId_EndedAt" ON "ActiveSessions" ("CompanyId", "EndedAt");

CREATE INDEX "IX_ActiveSessions_RevokedAt" ON "ActiveSessions" ("RevokedAt");

CREATE INDEX "IX_ActiveSessions_ServerId_EndedAt" ON "ActiveSessions" ("ServerId", "EndedAt");

CREATE UNIQUE INDEX "IX_ActiveSessions_SessionKey_ApplicationId" ON "ActiveSessions" ("SessionKey", "ApplicationId");

CREATE UNIQUE INDEX "IX_AdministratorRoles_AdministratorId_RoleId" ON "AdministratorRoles" ("AdministratorId", "RoleId");

CREATE INDEX "IX_AdministratorRoles_RoleId" ON "AdministratorRoles" ("RoleId");

CREATE UNIQUE INDEX "IX_Administrators_NormalizedUsername" ON "Administrators" ("NormalizedUsername");

CREATE INDEX "IX_Alerts_DedupKey_Status" ON "Alerts" ("DedupKey", "Status");

CREATE INDEX "IX_Alerts_Status_LastRaisedAt" ON "Alerts" ("Status", "LastRaisedAt");

CREATE INDEX "IX_ApplicationEnvironments_ApplicationId_Environment" ON "ApplicationEnvironments" ("ApplicationId", "Environment");

CREATE UNIQUE INDEX "IX_ApplicationEnvironments_PublicHost_PathPrefix" ON "ApplicationEnvironments" ("PublicHost", "PathPrefix");

CREATE UNIQUE INDEX "IX_Applications_Code" ON "Applications" ("Code");

CREATE INDEX "IX_AuditLogs_At" ON "AuditLogs" ("At");

CREATE INDEX "IX_AuditLogs_CompanyId_At" ON "AuditLogs" ("CompanyId", "At");

CREATE INDEX "IX_AuditLogs_EntityType_EntityId" ON "AuditLogs" ("EntityType", "EntityId");

CREATE INDEX "IX_AuditLogs_EventType_At" ON "AuditLogs" ("EventType", "At");

CREATE INDEX "IX_CapacityReservations_CompanyId" ON "CapacityReservations" ("CompanyId");

CREATE UNIQUE INDEX "IX_CapacityReservations_ServerId_CompanyId" ON "CapacityReservations" ("ServerId", "CompanyId");

CREATE INDEX "IX_Companies_Name" ON "Companies" ("Name");

CREATE UNIQUE INDEX "IX_Companies_NormalizedCode" ON "Companies" ("NormalizedCode");

CREATE INDEX "IX_Companies_RoutingPolicyId" ON "Companies" ("RoutingPolicyId");

CREATE INDEX "IX_CompanyAliases_CompanyId" ON "CompanyAliases" ("CompanyId");

CREATE UNIQUE INDEX "IX_CompanyAliases_NormalizedAlias" ON "CompanyAliases" ("NormalizedAlias");

CREATE INDEX "IX_CompanyServerAssignments_ApplicationId" ON "CompanyServerAssignments" ("ApplicationId");

CREATE INDEX "IX_CompanyServerAssignments_ClusterPoolId" ON "CompanyServerAssignments" ("ClusterPoolId");

CREATE UNIQUE INDEX "IX_CompanyServerAssignments_CompanyId_Environment_ApplicationId_EffectiveFrom" ON "CompanyServerAssignments" ("CompanyId", "Environment", "ApplicationId", "EffectiveFrom");

CREATE INDEX "IX_CompanyServerAssignments_CompanyId_IsEnabled_EffectiveTo" ON "CompanyServerAssignments" ("CompanyId", "IsEnabled", "EffectiveTo");

CREATE INDEX "IX_CompanyServerAssignments_FailoverPolicyId" ON "CompanyServerAssignments" ("FailoverPolicyId");

CREATE INDEX "IX_CompanyServerAssignments_PrimaryServerId" ON "CompanyServerAssignments" ("PrimaryServerId");

CREATE INDEX "IX_CompanyServerAssignments_SecondaryServerId" ON "CompanyServerAssignments" ("SecondaryServerId");

CREATE UNIQUE INDEX "IX_CompanyServerAuthorizations_CompanyId_ServerId" ON "CompanyServerAuthorizations" ("CompanyId", "ServerId");

CREATE INDEX "IX_CompanyServerAuthorizations_ServerId" ON "CompanyServerAuthorizations" ("ServerId");

CREATE INDEX "IX_ConfigurationVersions_ChangedAt" ON "ConfigurationVersions" ("ChangedAt");

CREATE INDEX "IX_FailoverPolicies_FailoverPoolId" ON "FailoverPolicies" ("FailoverPoolId");

CREATE UNIQUE INDEX "IX_FailoverPolicies_Name" ON "FailoverPolicies" ("Name");

CREATE UNIQUE INDEX "IX_GatewayInstances_InstanceName" ON "GatewayInstances" ("InstanceName");

CREATE INDEX "IX_GatewayTrafficSamples_At_GatewayInstance" ON "GatewayTrafficSamples" ("At", "GatewayInstance");

CREATE INDEX "IX_MaintenanceWindows_ServerId_StartsAt" ON "MaintenanceWindows" ("ServerId", "StartsAt");

CREATE INDEX "IX_MaintenanceWindows_Status_StartsAt" ON "MaintenanceWindows" ("Status", "StartsAt");

CREATE UNIQUE INDEX "IX_NotificationChannels_Name" ON "NotificationChannels" ("Name");

CREATE UNIQUE INDEX "IX_Permissions_Code" ON "Permissions" ("Code");

CREATE INDEX "IX_RolePermissions_PermissionId" ON "RolePermissions" ("PermissionId");

CREATE UNIQUE INDEX "IX_RolePermissions_RoleId_PermissionId" ON "RolePermissions" ("RoleId", "PermissionId");

CREATE UNIQUE INDEX "IX_Roles_Name" ON "Roles" ("Name");

CREATE INDEX "IX_RoutingDecisionLogs_At" ON "RoutingDecisionLogs" ("At");

CREATE INDEX "IX_RoutingDecisionLogs_CompanyId_At" ON "RoutingDecisionLogs" ("CompanyId", "At");

CREATE INDEX "IX_RoutingDecisionLogs_Outcome_At" ON "RoutingDecisionLogs" ("Outcome", "At");

CREATE INDEX "IX_RoutingDecisionLogs_ServerId_At" ON "RoutingDecisionLogs" ("ServerId", "At");

CREATE UNIQUE INDEX "IX_RoutingPolicies_Name" ON "RoutingPolicies" ("Name");

CREATE INDEX "IX_ServerApplications_ApplicationId" ON "ServerApplications" ("ApplicationId");

CREATE UNIQUE INDEX "IX_ServerApplications_ServerId_ApplicationId" ON "ServerApplications" ("ServerId", "ApplicationId");

CREATE INDEX "IX_ServerHealthHistory_ServerId_At" ON "ServerHealthHistory" ("ServerId", "At");

CREATE INDEX "IX_ServerMetrics_CapturedAt" ON "ServerMetrics" ("CapturedAt");

CREATE INDEX "IX_ServerMetrics_ServerId_CapturedAt" ON "ServerMetrics" ("ServerId", "CapturedAt");

CREATE INDEX "IX_ServerPoolApplications_ApplicationId" ON "ServerPoolApplications" ("ApplicationId");

CREATE UNIQUE INDEX "IX_ServerPoolApplications_PoolId_ApplicationId" ON "ServerPoolApplications" ("PoolId", "ApplicationId");

CREATE UNIQUE INDEX "IX_ServerPoolMembers_PoolId_ServerId" ON "ServerPoolMembers" ("PoolId", "ServerId");

CREATE INDEX "IX_ServerPoolMembers_ServerId" ON "ServerPoolMembers" ("ServerId");

CREATE INDEX "IX_ServerPools_FailoverPolicyId" ON "ServerPools" ("FailoverPolicyId");

CREATE UNIQUE INDEX "IX_ServerPools_Name" ON "ServerPools" ("Name");

CREATE INDEX "IX_Servers_Environment_AdminState" ON "Servers" ("Environment", "AdminState");

CREATE UNIQUE INDEX "IX_Servers_Name" ON "Servers" ("Name");

CREATE UNIQUE INDEX "IX_ServiceLeases_Name" ON "ServiceLeases" ("Name");

CREATE INDEX "IX_SharedPoolAssignments_ApplicationId" ON "SharedPoolAssignments" ("ApplicationId");

CREATE INDEX "IX_SharedPoolAssignments_CompanyId_IsEnabled_EffectiveTo" ON "SharedPoolAssignments" ("CompanyId", "IsEnabled", "EffectiveTo");

CREATE UNIQUE INDEX "IX_SharedPoolAssignments_CompanyId_PoolId_Environment_ApplicationId_EffectiveFrom" ON "SharedPoolAssignments" ("CompanyId", "PoolId", "Environment", "ApplicationId", "EffectiveFrom");

CREATE INDEX "IX_SharedPoolAssignments_PoolId" ON "SharedPoolAssignments" ("PoolId");

CREATE UNIQUE INDEX "IX_SystemSettings_Key" ON "SystemSettings" ("Key");

CREATE UNIQUE INDEX "IX_UserCompanyMemberships_CompanyId_ExternalSubject" ON "UserCompanyMemberships" ("CompanyId", "ExternalSubject") WHERE "ExternalSubject" IS NOT NULL;

CREATE UNIQUE INDEX "IX_UserCompanyMemberships_CompanyId_NormalizedUsername" ON "UserCompanyMemberships" ("CompanyId", "NormalizedUsername");

CREATE UNIQUE INDEX "IX_UserCompanyMemberships_CompanyId_UserId" ON "UserCompanyMemberships" ("CompanyId", "UserId");

CREATE INDEX "IX_UserCompanyMemberships_UserId" ON "UserCompanyMemberships" ("UserId");

CREATE INDEX "IX_Users_Email" ON "Users" ("Email");

CREATE INDEX "IX_UserServerAssignments_ApplicationId" ON "UserServerAssignments" ("ApplicationId");

CREATE INDEX "IX_UserServerAssignments_CompanyId_IsEnabled_EffectiveTo" ON "UserServerAssignments" ("CompanyId", "IsEnabled", "EffectiveTo");

CREATE UNIQUE INDEX "IX_UserServerAssignments_CompanyId_UserId_Environment_ApplicationId_EffectiveFrom" ON "UserServerAssignments" ("CompanyId", "UserId", "Environment", "ApplicationId", "EffectiveFrom");

CREATE INDEX "IX_UserServerAssignments_FailoverPolicyId" ON "UserServerAssignments" ("FailoverPolicyId");

CREATE INDEX "IX_UserServerAssignments_PrimaryServerId" ON "UserServerAssignments" ("PrimaryServerId");

CREATE INDEX "IX_UserServerAssignments_SecondaryServerId" ON "UserServerAssignments" ("SecondaryServerId");

CREATE INDEX "IX_UserServerAssignments_UserId" ON "UserServerAssignments" ("UserId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261027172026_InitialCreate', '10.0.14');

COMMIT;

