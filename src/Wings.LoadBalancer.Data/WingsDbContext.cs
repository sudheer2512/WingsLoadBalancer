using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Wings.LoadBalancer.Data.Entities;

namespace Wings.LoadBalancer.Data;

public class WingsDbContext(DbContextOptions options) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>ASP.NET Core Data Protection key ring, shared by all gateway and admin instances.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyAlias> CompanyAliases => Set<CompanyAlias>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserCompanyMembership> UserCompanyMemberships => Set<UserCompanyMembership>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<ApplicationEnvironment> ApplicationEnvironments => Set<ApplicationEnvironment>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<ServerApplication> ServerApplications => Set<ServerApplication>();
    public DbSet<ServerPool> ServerPools => Set<ServerPool>();
    public DbSet<ServerPoolApplication> ServerPoolApplications => Set<ServerPoolApplication>();
    public DbSet<ServerPoolMember> ServerPoolMembers => Set<ServerPoolMember>();
    public DbSet<CompanyServerAssignment> CompanyServerAssignments => Set<CompanyServerAssignment>();
    public DbSet<UserServerAssignment> UserServerAssignments => Set<UserServerAssignment>();
    public DbSet<SharedPoolAssignment> SharedPoolAssignments => Set<SharedPoolAssignment>();
    public DbSet<FailoverPolicy> FailoverPolicies => Set<FailoverPolicy>();
    public DbSet<ServerHealthHistory> ServerHealthHistory => Set<ServerHealthHistory>();
    public DbSet<ServerHealthCurrent> ServerHealthCurrent => Set<ServerHealthCurrent>();
    public DbSet<ServerMetric> ServerMetrics => Set<ServerMetric>();
    public DbSet<RoutingPolicy> RoutingPolicies => Set<RoutingPolicy>();
    public DbSet<RoutingDecisionLog> RoutingDecisionLogs => Set<RoutingDecisionLog>();
    public DbSet<ActiveSession> ActiveSessions => Set<ActiveSession>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Administrator> Administrators => Set<Administrator>();
    public DbSet<AdministratorRole> AdministratorRoles => Set<AdministratorRole>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<CapacityReservation> CapacityReservations => Set<CapacityReservation>();
    public DbSet<CompanyServerAuthorization> CompanyServerAuthorizations => Set<CompanyServerAuthorization>();
    public DbSet<GatewayTrafficSample> GatewayTrafficSamples => Set<GatewayTrafficSample>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<GatewayInstance> GatewayInstances => Set<GatewayInstance>();
    public DbSet<ServiceLease> ServiceLeases => Set<ServiceLease>();
    public DbSet<ConfigurationVersion> ConfigurationVersions => Set<ConfigurationVersion>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enums stored as readable strings in both providers.
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
        builder.Properties<string>().HaveMaxLength(400);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            if (typeof(IConcurrencyStamped).IsAssignableFrom(entityType.ClrType))
            {
                b.Entity(entityType.ClrType).Property(nameof(IConcurrencyStamped.ConcurrencyStamp)).IsConcurrencyToken();
            }
            if (typeof(Entity).IsAssignableFrom(entityType.ClrType))
            {
                b.Entity(entityType.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();
            }
        }

        b.Entity<Company>(e =>
        {
            e.HasIndex(x => x.NormalizedCode).IsUnique();
            e.HasIndex(x => x.Name);
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.NormalizedCode).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.HasOne(x => x.RoutingPolicy).WithMany().HasForeignKey(x => x.RoutingPolicyId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<CompanyAlias>(e =>
        {
            e.HasIndex(x => x.NormalizedAlias).IsUnique();
            e.HasOne(x => x.Company).WithMany(c => c.Aliases).HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.MfaSecretProtected).HasMaxLength(1024);
        });
        b.Entity<UserCompanyMembership>(e =>
        {
            // Tenant-scoped identity: a username is unique only within its company.
            e.HasIndex(x => new { x.CompanyId, x.NormalizedUsername }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.ExternalSubject }).IsUnique().HasFilter("[ExternalSubject] IS NOT NULL");
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Application>(e => e.HasIndex(x => x.Code).IsUnique());
        b.Entity<ApplicationEnvironment>(e =>
        {
            e.HasIndex(x => new { x.PublicHost, x.PathPrefix }).IsUnique();
            e.HasIndex(x => new { x.ApplicationId, x.Environment });
            e.HasOne(x => x.Application).WithMany(a => a.Environments).HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Server>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => new { x.Environment, x.AdminState });
            e.Property(x => x.BaseUrl).HasMaxLength(1000);
            e.Property(x => x.PublicUrl).HasMaxLength(1000);
            e.Property(x => x.HealthCheckUrl).HasMaxLength(1000);
            e.Property(x => x.Notes).HasMaxLength(4000);
        });
        b.Entity<ServerApplication>(e =>
        {
            e.HasIndex(x => new { x.ServerId, x.ApplicationId }).IsUnique();
            e.HasOne(x => x.Server).WithMany(s => s.Applications).HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ServerPool>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.FailoverPolicy).WithMany().HasForeignKey(x => x.FailoverPolicyId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ServerPoolApplication>(e =>
        {
            e.HasIndex(x => new { x.PoolId, x.ApplicationId }).IsUnique();
            e.HasOne(x => x.Pool).WithMany(p => p.Applications).HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ServerPoolMember>(e =>
        {
            e.HasIndex(x => new { x.PoolId, x.ServerId }).IsUnique();
            e.HasIndex(x => x.ServerId);
            e.HasOne(x => x.Pool).WithMany(p => p.Members).HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        });

        ConfigureAssignment(b.Entity<CompanyServerAssignment>());
        b.Entity<CompanyServerAssignment>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Environment, x.ApplicationId, x.EffectiveFrom }).IsUnique();
            e.HasIndex(x => x.PrimaryServerId);
            e.HasOne(x => x.PrimaryServer).WithMany().HasForeignKey(x => x.PrimaryServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SecondaryServer).WithMany().HasForeignKey(x => x.SecondaryServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ClusterPool).WithMany().HasForeignKey(x => x.ClusterPoolId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FailoverPolicy).WithMany().HasForeignKey(x => x.FailoverPolicyId).OnDelete(DeleteBehavior.Restrict);
        });
        ConfigureAssignment(b.Entity<UserServerAssignment>());
        b.Entity<UserServerAssignment>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.UserId, x.Environment, x.ApplicationId, x.EffectiveFrom }).IsUnique();
            e.HasIndex(x => x.PrimaryServerId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PrimaryServer).WithMany().HasForeignKey(x => x.PrimaryServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SecondaryServer).WithMany().HasForeignKey(x => x.SecondaryServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FailoverPolicy).WithMany().HasForeignKey(x => x.FailoverPolicyId).OnDelete(DeleteBehavior.Restrict);
        });
        ConfigureAssignment(b.Entity<SharedPoolAssignment>());
        b.Entity<SharedPoolAssignment>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.PoolId, x.Environment, x.ApplicationId, x.EffectiveFrom }).IsUnique();
            e.HasIndex(x => x.PoolId);
            e.HasOne(x => x.Pool).WithMany().HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<FailoverPolicy>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.FailoverPool).WithMany().HasForeignKey(x => x.FailoverPoolId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<RoutingPolicy>(e => e.HasIndex(x => x.Name).IsUnique());
        b.Entity<CapacityReservation>(e =>
        {
            e.HasIndex(x => new { x.ServerId, x.CompanyId }).IsUnique();
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CompanyServerAuthorization>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.ServerId }).IsUnique();
            e.HasIndex(x => x.ServerId);
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<GatewayTrafficSample>(e => e.HasIndex(x => new { x.At, x.GatewayInstance }));
        b.Entity<ServerHealthHistory>(e => e.HasIndex(x => new { x.ServerId, x.At }));
        b.Entity<ServerHealthCurrent>(e => e.HasKey(x => x.ServerId));
        b.Entity<ServerMetric>(e =>
        {
            e.HasIndex(x => new { x.ServerId, x.CapturedAt });
            e.HasIndex(x => x.CapturedAt);
        });
        b.Entity<RoutingDecisionLog>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.CompanyId, x.At });
            e.HasIndex(x => new { x.ServerId, x.At });
            e.HasIndex(x => new { x.Outcome, x.At });
            e.Property(x => x.Reason).HasMaxLength(1000);
        });
        b.Entity<ActiveSession>(e =>
        {
            e.HasIndex(x => new { x.SessionKey, x.ApplicationId }).IsUnique();
            e.HasIndex(x => new { x.ServerId, x.EndedAt });
            e.HasIndex(x => new { x.CompanyId, x.EndedAt });
            e.HasIndex(x => x.RevokedAt);
            e.Property(x => x.SessionKey).HasMaxLength(88);
        });
        b.Entity<MaintenanceWindow>(e =>
        {
            e.HasIndex(x => new { x.ServerId, x.StartsAt });
            e.HasIndex(x => new { x.Status, x.StartsAt });
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.EventType, x.At });
            e.HasIndex(x => new { x.CompanyId, x.At });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.PreviousValue).HasMaxLength(8000);
            e.Property(x => x.NewValue).HasMaxLength(8000);
            e.Property(x => x.Reason).HasMaxLength(2000);
        });

        b.Entity<Administrator>(e => e.HasIndex(x => x.NormalizedUsername).IsUnique());
        b.Entity<Role>(e => e.HasIndex(x => x.Name).IsUnique());
        b.Entity<Permission>(e => e.HasIndex(x => x.Code).IsUnique());
        b.Entity<AdministratorRole>(e =>
        {
            e.HasIndex(x => new { x.AdministratorId, x.RoleId }).IsUnique();
            e.HasOne(x => x.Administrator).WithMany(a => a.Roles).HasForeignKey(x => x.AdministratorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RolePermission>(e =>
        {
            e.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique();
            e.HasOne(x => x.Role).WithMany(r => r.Permissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SystemSetting>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Value).HasMaxLength(4000);
        });
        b.Entity<Alert>(e =>
        {
            e.HasIndex(x => new { x.DedupKey, x.Status });
            e.HasIndex(x => new { x.Status, x.LastRaisedAt });
            e.Property(x => x.Message).HasMaxLength(4000);
        });
        b.Entity<NotificationChannel>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Target).HasMaxLength(2000);
        });
        b.Entity<GatewayInstance>(e => e.HasIndex(x => x.InstanceName).IsUnique());
        b.Entity<ServiceLease>(e => e.HasIndex(x => x.Name).IsUnique());
        b.Entity<DataProtectionKey>(e =>
        {
            e.Property(x => x.FriendlyName).HasMaxLength(400);
            e.Property(x => x.Xml).HasMaxLength(-1);
        });
        b.Entity<ConfigurationVersion>(e =>
        {
            e.HasIndex(x => x.ChangedAt);
            e.Property(x => x.Summary).HasMaxLength(2000);
        });
    }

    private static void ConfigureAssignment<T>(EntityTypeBuilder<T> e) where T : AssignmentBase
    {
        e.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.CompanyId, x.IsEnabled, x.EffectiveTo });
        e.Property(x => x.Reason).HasMaxLength(1000);
    }
}

/// <summary>SQL Server model (production). Migrations live in Migrations/SqlServer.</summary>
public sealed class SqlServerWingsDbContext(DbContextOptions<SqlServerWingsDbContext> options) : WingsDbContext(options);

/// <summary>SQLite model (local development and tests). Migrations live in Migrations/Sqlite.</summary>
public sealed class SqliteWingsDbContext(DbContextOptions<SqliteWingsDbContext> options) : WingsDbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        base.ConfigureConventions(builder);
        // SQLite cannot compare or order DateTimeOffset natively; store as sortable UTC ticks.
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<UserCompanyMembership>().HasIndex(x => new { x.CompanyId, x.ExternalSubject }).IsUnique()
            .HasFilter("\"ExternalSubject\" IS NOT NULL");
    }
}
