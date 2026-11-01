// Shapes returned by the Wings admin API. Enums are serialised as strings by the backend (JsonStringEnumConverter).

export type Environment = 'Production' | 'Staging' | 'Test' | 'Development';
export const ENVIRONMENTS: Environment[] = ['Production', 'Staging', 'Test', 'Development'];
export type ServerType = 'Dedicated' | 'Shared' | 'Hybrid';
export const SERVER_TYPES: ServerType[] = ['Dedicated', 'Shared', 'Hybrid'];
export type ServerAdminState = 'Active' | 'Draining' | 'Maintenance' | 'Disabled' | 'Retired';
export const ADMIN_STATES: ServerAdminState[] = ['Active', 'Draining', 'Maintenance', 'Disabled', 'Retired'];
export type HealthState = 'Unknown' | 'Healthy' | 'Degraded' | 'Unhealthy' | 'Recovering';
export const HEALTH_STATES: HealthState[] = ['Unknown', 'Healthy', 'Degraded', 'Unhealthy', 'Recovering'];
export type CompanyStatus = 'Active' | 'Suspended' | 'Disabled';
export const COMPANY_STATUSES: CompanyStatus[] = ['Active', 'Suspended', 'Disabled'];
export type CompanyRoutingMode = 'Dedicated' | 'SharedPool' | 'Cluster';
export const COMPANY_ROUTING_MODES: CompanyRoutingMode[] = ['Dedicated', 'SharedPool', 'Cluster'];
export type ClusterMode = 'PrimaryPreferred' | 'Balanced';
export const CLUSTER_MODES: ClusterMode[] = ['PrimaryPreferred', 'Balanced'];
export type Algorithm = 'RoundRobin' | 'WeightedRoundRobin' | 'LeastRequests' | 'WeightedLeastRequests' | 'LowestUtilization'
  | 'ConsistentHashCompany' | 'ConsistentHashCompanyUser';
export const ALGORITHMS: Algorithm[] = ['RoundRobin', 'WeightedRoundRobin', 'LeastRequests', 'WeightedLeastRequests', 'LowestUtilization',
  'ConsistentHashCompany', 'ConsistentHashCompanyUser'];
export type AffinityMode = 'None' | 'PerUser' | 'PerCompany' | 'PerSession';
export const AFFINITY_MODES: AffinityMode[] = ['None', 'PerUser', 'PerCompany', 'PerSession'];
export type FailbackPolicy = 'Automatic' | 'EndOfSession' | 'Manual';
export const FAILBACK_POLICIES: FailbackPolicy[] = ['Automatic', 'EndOfSession', 'Manual'];
export type AppRoutingMode = 'Proxy' | 'Redirect';
export const APP_ROUTING_MODES: AppRoutingMode[] = ['Proxy', 'Redirect'];
export type RoutingOutcome = 'Routed' | 'Denied' | 'NoAssignment' | 'NoHealthyDestination' | 'ConfigurationStale';
export const ROUTING_OUTCOMES: RoutingOutcome[] = ['Routed', 'Denied', 'NoAssignment', 'NoHealthyDestination', 'ConfigurationStale'];
export type AuthenticationMode = 'Local' | 'WingsAuthService' | 'Oidc';
export const AUTH_MODES: AuthenticationMode[] = ['Local', 'WingsAuthService', 'Oidc'];
export type AccountStatus = 'Active' | 'Disabled';
export type AlertSeverity = 'Info' | 'Warning' | 'Critical';
export const ALERT_SEVERITIES: AlertSeverity[] = ['Info', 'Warning', 'Critical'];
export type AlertStatus = 'Open' | 'Acknowledged' | 'Resolved';
export const ALERT_STATUSES: AlertStatus[] = ['Open', 'Acknowledged', 'Resolved'];
export type ChannelType = 'Email' | 'Webhook' | 'Teams';
export const CHANNEL_TYPES: ChannelType[] = ['Email', 'Webhook', 'Teams'];
export type CutoverMode = 'Immediate' | 'Scheduled' | 'ForceReauthentication';
export type AssignmentState = 'Current' | 'Scheduled' | 'Ended' | 'Disabled';

export interface Me {
  authenticated: boolean;
  mfaRequired?: boolean;
  username?: string;
  displayName?: string;
  permissions?: string[];
  mfaEnabled?: boolean;
}

/** Error bodies: {error, message?} or {error:'validation_failed', errors:[...]}. */
export interface ApiError { error?: string; message?: string; errors?: string[]; blockers?: string[]; }

// ---------------------------------------------------------------- dashboard

export interface GatewayInfo {
  instanceName: string; host?: string; version?: string; lastHeartbeatAt: string; online: boolean; configVersion: number;
  requestsPerSecond: number; averageLatencyMs: number; activeRequests: number; redisAvailable: boolean; databaseAvailable: boolean;
  configurationStale: boolean;
}

export interface DashboardSummary {
  generatedAt: string;
  servers: { total: number; healthy: number; degraded: number; offline: number; draining: number; unknown: number };
  companies: { total: number; dedicated: number; sharedPools: number };
  usersOnDedicatedServers: number;
  activeSessions: number;
  requestsPerSecond: number;
  failedRoutingLastHour: number;
  averageGatewayLatencyMs: number;
  averageDecisionMicroseconds: number;
  currentFailovers: number;
  openAlerts: number;
  gateways: GatewayInfo[];
}

export interface UtilizationPoint { at: string; cpu?: number | null; memory?: number | null; requests?: number | null; responseMs?: number | null; errors?: number | null; sessions?: number | null; }
export interface ServerUtilization {
  id: string; name: string; environment: Environment; type: ServerType; adminState: ServerAdminState; health: HealthState;
  maxCapacity: number; applicationVersion?: string;
  current: {
    cpuPercent?: number | null; memoryPercent?: number | null; diskPercent?: number | null; activeRequests?: number | null;
    activeSessions?: number | null; responseTimeMs?: number | null; errorRatePercent?: number | null; lastCheckAt?: string;
    lastReason?: string; reportedVersion?: string; awaitingManualRestore?: boolean;
  } | null;
  series: UtilizationPoint[];
}

export interface TimeSeriesBucket { at: string; routedDecisions: number; failedDecisions: number; failovers: number; requests: number; averageLatencyMs: number; }

export interface TopologyNode {
  id: string; label: string; kind: 'environment' | 'pool' | 'group' | 'server' | 'company';
  children?: TopologyNode[];
  algorithm?: string; affinity?: string; tenants?: string[];
  type?: string; adminState?: string; health?: string; dedicatedUsers?: number; role?: string;
}

export interface DashboardFilter {
  companyId?: string; serverId?: string; poolId?: string; applicationId?: string; environment?: Environment | '';
  from?: string; to?: string; username?: string;
}

/** Pushed by the monitoring hub ("decisions"). */
export interface LiveDecision {
  id: string; at: string; eventKind?: string; outcome: RoutingOutcome; companyId?: string; company?: string; server?: string; serverId?: string;
  source: string; isFailover: boolean; isFailback: boolean; reason?: string; gatewayInstance?: string; environment?: string;
}
/** Pushed by the monitoring hub ("health"). */
export interface LiveHealth { serverId: string; state: HealthState; stateChangedAt?: string; lastReason?: string; responseTimeMs?: number; cpuPercent?: number; memoryPercent?: number; }

// ---------------------------------------------------------------- servers

export interface Server {
  id: string; name: string; hostname: string; ipAddress?: string | null; baseUrl: string; publicUrl?: string | null; healthCheckUrl?: string | null;
  environment: Environment; region?: string | null; serverType: ServerType; cpuCores?: number | null; ramGb?: number | null; maxCapacity: number;
  applicationVersion?: string | null; adminState: ServerAdminState; status: string; priority: number; weight: number;
  requireManualRestore: boolean; drainStartedAt?: string | null; drainTimeoutMinutes: number; clientCertificateThumbprint?: string | null;
  backendCertificateThumbprint?: string | null; notes?: string | null; createdAt: string; modifiedAt: string; modifiedBy?: string;
  retiredAt?: string | null; concurrencyStamp: string; applicationIds: string[]; pools: string[]; health: HealthState;
  lastHeartbeat?: string | null; lastCheckAt?: string | null; lastReason?: string | null; responseTimeMs?: number | null;
  cpuPercent?: number | null; memoryPercent?: number | null; activeRequests?: number | null; activeSessions?: number | null;
  errorRatePercent?: number | null; reportedVersion?: string | null; awaitingManualRestore: boolean;
}

export interface ServerRequest {
  name: string; hostname: string; ipAddress?: string | null; baseUrl: string; publicUrl?: string | null; healthCheckUrl?: string | null;
  environment: Environment; region?: string | null; serverType: ServerType; cpuCores?: number | null; ramGb?: number | null; maxCapacity: number;
  applicationVersion?: string | null; priority: number; weight: number; requireManualRestore: boolean; drainTimeoutMinutes: number;
  clientCertificateThumbprint?: string | null; backendCertificateThumbprint?: string | null; notes?: string | null;
  applicationIds: string[]; concurrencyStamp?: string;
}

export interface HealthCurrent {
  serverId: string; state: HealthState; stateChangedAt?: string; lastCheckAt?: string; lastHeartbeatAt?: string; lastReason?: string;
  consecutiveFailures: number; responseTimeMs?: number; cpuPercent?: number; memoryPercent?: number; diskPercent?: number;
  activeConnections?: number; activeRequests?: number; queueLength?: number; errorRatePercent?: number; activeSessions?: number;
  databaseHealthy?: boolean; reportedVersion?: string; awaitingManualRestore: boolean;
}
export interface HealthHistoryRow { id: string; serverId: string; fromState: HealthState; toState: HealthState; at: string; reason?: string; source: string; reportedBy?: string; }
export interface ServerMetric {
  id: string; serverId: string; capturedAt: string; cpuPercent?: number | null; memoryPercent?: number | null; diskPercent?: number | null;
  activeConnections?: number | null; activeRequests?: number | null; queueLength?: number | null; errorRatePercent?: number | null;
  responseTimeMs?: number | null; activeSessions?: number | null; databaseHealthy?: boolean | null;
}
export interface ServerAssignments {
  company: { id: string; companyId: string; company: string; role: string; environment: string; effectiveFrom: string; effectiveTo?: string }[];
  users: { id: string; companyId: string; userId: string; company: string; username: string; role: string; environment: string; effectiveFrom: string; effectiveTo?: string }[];
  pools: { poolId: string; pool: string; priority: number; weightOverride?: number; acceptingNewSessions: boolean }[];
  reservations: { id: string; companyId: string; company: string; reservedSessions: number }[];
  authorizations: { id: string; companyId: string; company: string; grantedAt: string; grantedBy?: string }[];
}
export interface ServerSession {
  sessionKey: string; companyId: string; company: string; userId: string; username: string; applicationId: string; startedAt: string;
  lastSeenAt: string; gatewayInstance?: string; clientAddress?: string;
}
export interface ConnectivityResult { success: boolean; responseTimeMs?: number; failure?: string; metrics?: Record<string, unknown>; }
export interface BackendConfig { serverId: string; signingKey: string; healthKey: string; note: string; }

export interface AuditRow {
  id: string; at: string; actor: string; actorType: string; eventType: string; entityType?: string; entityId?: string;
  companyId?: string; userId?: string; serverId?: string; previousValue?: string; newValue?: string; reason?: string;
  correlationId?: string; sourceIp?: string; severity: AlertSeverity;
}

// ---------------------------------------------------------------- companies

export interface CompanyListItem {
  id: string; code: string; name: string; servicePlan?: string; routingMode: CompanyRoutingMode; status: CompanyStatus;
  defaultEnvironment: Environment; maxConcurrentUsers?: number; authenticationMode: AuthenticationMode; aliases: string[];
  primaryServer?: string; secondaryServer?: string; failoverPolicy?: string; sharedPool?: string; users: number; activeUsers: number;
  createdAt: string; modifiedAt: string; concurrencyStamp: string;
}

export interface CompanyAssignment {
  id: string; environment: Environment; applicationId?: string | null; application?: string | null; primaryServerId: string; primaryServer?: string;
  secondaryServerId?: string | null; secondaryServer?: string | null; clusterPoolId?: string | null; clusterPool?: string | null;
  clusterMode: ClusterMode; failoverPolicyId?: string | null; failoverPolicy?: string | null; effectiveFrom: string; effectiveTo?: string | null;
  reason?: string | null; createdBy?: string; createdAt: string; state: AssignmentState;
}
export interface PoolAssignment {
  id: string; environment: Environment; applicationId?: string | null; application?: string | null; poolId: string; pool?: string;
  priority: number; effectiveFrom: string; effectiveTo?: string | null; reason?: string | null; createdBy?: string; state: AssignmentState;
}
export interface CompanyDetail {
  id: string; code: string; name: string; servicePlan?: string | null; routingMode: CompanyRoutingMode; status: CompanyStatus;
  defaultEnvironment: Environment; maxConcurrentUsers?: number | null; authenticationMode: AuthenticationMode; routingPolicyId?: string | null;
  routingPolicy?: string | null; notes?: string | null; aliases: string[]; createdAt: string; modifiedAt: string; modifiedBy?: string;
  concurrencyStamp: string; companyAssignments: CompanyAssignment[]; poolAssignments: PoolAssignment[];
  explicitAuthorizations: { id: string; serverId: string; server: string; grantedAt: string; grantedBy?: string; notes?: string }[];
  authorizedServerIds: string[];
}
export interface CompanyRequest {
  code: string; name: string; servicePlan?: string | null; routingMode: CompanyRoutingMode; status: CompanyStatus;
  defaultEnvironment: Environment; maxConcurrentUsers?: number | null; authenticationMode: AuthenticationMode;
  routingPolicyId?: string | null; notes?: string | null; aliases: string[]; concurrencyStamp?: string;
}
export interface CompanyAssignmentRequest {
  primaryServerId: string; secondaryServerId?: string | null; clusterPoolId?: string | null; clusterMode: ClusterMode;
  failoverPolicyId?: string | null; applicationId?: string | null; environment: Environment; effectiveFrom?: string | null;
  effectiveTo?: string | null; reason?: string | null; expectedStamp?: string;
}
export interface SharedPoolAssignmentRequest {
  poolId: string; applicationId?: string | null; environment: Environment; priority: number; effectiveFrom?: string | null;
  effectiveTo?: string | null; reason?: string | null; replaceDedicated: boolean; expectedStamp?: string;
}
export interface TransferRequest {
  targetServerId: string; targetSecondaryServerId?: string | null; environment: Environment; applicationId?: string | null;
  mode: CutoverMode; scheduledAt?: string | null; validateOnly: boolean; reason?: string | null; expectedStamp?: string;
}
export interface TransferCheck { check: string; passed: boolean; detail: string; }
export interface TransferResult { passed: boolean; applied: boolean; assignmentId?: string; checks: TransferCheck[]; }

export interface PreviewRow {
  company: string; username: string; application?: string; environment: string; changed: boolean;
  before: { outcome: string; server?: string; reason: string }; after: { outcome: string; server?: string; reason: string };
}
export interface PreviewResult { validationErrors: string[]; preview: { results: PreviewRow[]; issues: string[] }; }

export interface CompanyUser { userId: string; username: string; displayName: string; email?: string; status: AccountStatus; userStatus: AccountStatus; lastLoginAt?: string; mfaEnabled: boolean; }
export interface CompanySession { sessionKey: string; username: string; applicationId: string; serverId?: string; startedAt: string; lastSeenAt: string; gatewayInstance?: string; }

// ---------------------------------------------------------------- users

export interface UserRow {
  userId: string; companyId: string; company: string; companyName: string; username: string; displayName: string; email?: string;
  status: AccountStatus; userStatus: AccountStatus; lastLoginAt?: string; membershipStamp: string; assignedServer?: string | null;
  secondaryServer?: string | null; routingType: string; assignmentId?: string | null;
}
export interface UserAssignment {
  id: string; environment: Environment; applicationId?: string | null; application?: string | null; primaryServerId: string; primaryServer?: string;
  secondaryServerId?: string | null; secondaryServer?: string | null; failoverPolicyId?: string | null; failoverPolicy?: string | null;
  allowCompanyRouteFallback: boolean; isEnabled: boolean; effectiveFrom: string; effectiveTo?: string | null; reason?: string | null;
  createdAt: string; createdBy?: string; supersededById?: string | null; state: AssignmentState;
}
export interface UserAssignmentView {
  userId: string; companyId: string; company: string; username: string; membershipStamp: string; assignments: UserAssignment[];
  authorizedServerIds: string[];
}
export interface UserAssignmentRequest {
  companyId: string; primaryServerId: string; secondaryServerId?: string | null; failoverPolicyId?: string | null;
  applicationId?: string | null; environment: Environment; allowCompanyRouteFallback: boolean; isEnabled: boolean;
  effectiveFrom?: string | null; effectiveTo?: string | null; reason?: string | null; expectedStamp?: string;
}
export interface ImportRow { row: number; company: string; username: string; action: string; valid: boolean; errors: string[]; summary?: string; }
export interface ImportResult { dryRun: boolean; valid: boolean; total: number; applied: number; rows: ImportRow[]; }

// ---------------------------------------------------------------- pools, policies, applications

export interface PoolMember {
  id: string; serverId: string; server: string; weightOverride?: number | null; effectiveWeight: number; priority: number;
  acceptingNewSessions: boolean; adminState: ServerAdminState; health: HealthState;
}
export interface Pool {
  id: string; name: string; description?: string | null; environment: Environment; region?: string | null; algorithm: Algorithm;
  affinity: AffinityMode; failoverPolicyId?: string | null; failoverPolicy?: string | null; maxCapacity?: number | null;
  eligiblePlans: string[]; applicationIds: string[]; concurrencyStamp: string; tenants: number; members: PoolMember[];
}
export interface PoolRequest {
  name: string; description?: string | null; environment: Environment; region?: string | null; algorithm: Algorithm; affinity: AffinityMode;
  failoverPolicyId?: string | null; maxCapacity?: number | null; eligiblePlans: string[]; applicationIds: string[]; concurrencyStamp?: string;
}
export interface PoolMemberRequest { serverId: string; weightOverride?: number | null; priority: number; acceptingNewSessions: boolean; }

export interface FailoverPolicy {
  id: string; name: string; description?: string | null; failoverEnabled: boolean; failoverPoolId?: string | null; failoverPool?: string | null;
  failbackPolicy: FailbackPolicy; maxFailoverAttempts: number; failoverPriority: number; concurrencyStamp: string;
}
export interface RoutingPolicy {
  id: string; name: string; isDefault: boolean; allowDegradedServers: boolean; defaultAffinity: AffinityMode; cpuWeight: number;
  memoryWeight: number; activeRequestsWeight: number; responseTimeWeight: number; errorRateWeight: number; responseTimeCeilingMs: number;
  sessionIdleTimeoutMinutes: number; sessionAbsoluteTimeoutHours: number; concurrencyStamp: string;
}
export interface AppBinding {
  id: string; environment: Environment; publicHost: string; pathPrefix: string; routingMode: AppRoutingMode; isEnabled: boolean;
  requestTimeoutSeconds: number; maxRequestBodyBytes?: number | null;
}
export interface Application {
  id: string; code: string; name: string; description?: string | null; isEnabled: boolean; minimumServerVersion?: string | null;
  concurrencyStamp: string; environments: AppBinding[];
}

// ---------------------------------------------------------------- routing

export interface SimulationRequest { company: string; username: string; application?: string | null; environment: Environment; }
export interface ServerSummary { id: string; name: string; adminState: string; health: string; type: string; environment: string; }
export interface SimulationResult {
  outcome: RoutingOutcome; reason: string; configVersion: number; companyId?: string; companyCode?: string; companyName?: string;
  companyStatus?: string; userId?: string; membershipStatus?: string; applicationId?: string; applicationCode?: string;
  userAssignment?: { id: string; primary?: ServerSummary; secondary?: ServerSummary; effectiveFrom: string; effectiveTo?: string; allowCompanyRouteFallback: boolean } | null;
  companyAssignment?: { id: string; primary?: ServerSummary; secondary?: ServerSummary; clusterPoolId?: string; clusterMode: string; effectiveFrom: string; effectiveTo?: string } | null;
  eligiblePools: { id: string; name: string; algorithm?: string; affinity?: string; priority?: number; members?: number }[];
  routingPolicy?: string; failoverPolicy?: string; source?: string; chosenServer?: ServerSummary | null; failoverServer?: ServerSummary | null;
  isFailover: boolean; selectionDetail?: string; trace: string[];
}
export interface E2eResult { success: boolean; steps: { step: string; status: number; ms: number; servedBy?: string }[]; }

export interface RoutingHistoryRow {
  id: string; at: string; eventKind?: string; outcome: RoutingOutcome; companyId?: string; company?: string; userId?: string; username?: string;
  serverId?: string; server?: string; source: string; poolId?: string; applicationId?: string; environment?: string; isFailover: boolean;
  isFailback: boolean; affinityApplied: boolean; reason?: string; configVersion: number; decisionMicroseconds: number;
  gatewayInstance?: string; correlationId?: string;
}
export interface Paged<T> { total: number; rows: T[]; }

export interface RoutingConfig {
  version: number;
  gateways: { instanceName: string; configVersion: number; lastHeartbeatAt: string; inSync: boolean }[];
  versions: { id: string; version: number; changedAt: string; changedBy?: string; summary?: string }[];
  issues: { severity: string; code: string; message: string; companyId?: string; serverId?: string }[];
}

// ---------------------------------------------------------------- operations

export interface MaintenanceWindow {
  id: string; serverId: string; server: string; startsAt: string; endsAt: string; drainTimeoutMinutes: number; autoRestore: boolean;
  status: string; reason?: string; createdBy?: string; createdAt: string;
}
export interface ActiveSession {
  id: string; sessionKey: string; companyId: string; company: string; userId: string; username: string; applicationId: string;
  application?: string; serverId?: string; server?: string; startedAt: string; lastSeenAt: string; gatewayInstance?: string;
  clientAddress?: string; userAgent?: string;
}
export interface Alert {
  id: string; type: string; severity: AlertSeverity; status: AlertStatus; title: string; message?: string; serverId?: string; server?: string;
  companyId?: string; company?: string; firstRaisedAt: string; lastRaisedAt: string; occurrences: number; acknowledgedAt?: string;
  acknowledgedBy?: string; resolvedAt?: string; resolvedBy?: string; resolutionNote?: string;
}
export interface Channel { id: string; name: string; type: ChannelType; target: string; minimumSeverity: AlertSeverity; isEnabled: boolean; }
export interface ReportDefinition { name: string; title: string; description: string; }
export interface ReportTable { name: string; title: string; columns: string[]; rows: unknown[][]; }
export interface Setting { id: string; key: string; value: string; description?: string; modifiedAt: string; modifiedBy?: string; }
export interface Administrator {
  id: string; username: string; displayName: string; email?: string; status: AccountStatus; mfaEnabled: boolean; lastLoginAt?: string;
  lockoutEndAt?: string; roles: string[];
}
export interface Role { id: string; name: string; description?: string; isSystem: boolean; permissions: string[]; }
export interface PermissionInfo { code: string; description: string; }
