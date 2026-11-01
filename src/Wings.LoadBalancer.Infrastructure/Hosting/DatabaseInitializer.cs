using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.Routing;
using Wings.Routing.Model;

namespace Wings.LoadBalancer.Infrastructure.Hosting;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>Apply EF migrations at startup (development / single-node). Production uses the SQL scripts.</summary>
    public bool ApplyMigrations { get; set; } = true;

    /// <summary>Create the sample companies, users, servers and pools (development and tests only).</summary>
    public bool SampleData { get; set; }

    /// <summary>Initial administrator; only created when no administrator exists. Password from a secret store.</summary>
    public string AdminUsername { get; set; } = "admin";
    public string? AdminPassword { get; set; }

    /// <summary>Password for all sample users (development only).</summary>
    public string? SampleUserPassword { get; set; }

    /// <summary>Base URL template for sample backends; {port} (5101–5108) and {name} (app-server-01 …) are replaced.</summary>
    public string SampleBackendUrlTemplate { get; set; } = "http://localhost:{port}";

    /// <summary>Public host the sample ERP production binding uses.</summary>
    public string SampleProductionHost { get; set; } = "localhost";
    public string SampleStagingHost { get; set; } = "staging.localhost";
}

/// <summary>Deterministic ids for the sample data (documentation, tests and the Docker environment).</summary>
public static class SampleIds
{
    public static readonly Guid ErpApp = new("0192f000-0000-7000-8000-000000000001");
    public static readonly Guid HrApp = new("0192f000-0000-7000-8000-000000000002");

    public static readonly Guid AbcMotors = new("0192f000-0000-7000-8000-000000000101");
    public static readonly Guid XyzMotors = new("0192f000-0000-7000-8000-000000000102");
    public static readonly Guid GlobalFreight = new("0192f000-0000-7000-8000-000000000103");

    public static readonly Guid AbcUser1 = new("0192f000-0000-7000-8000-000000000201");
    public static readonly Guid AbcUser2 = new("0192f000-0000-7000-8000-000000000202");
    public static readonly Guid AbcManager = new("0192f000-0000-7000-8000-000000000203");
    public static readonly Guid AbcAccounts = new("0192f000-0000-7000-8000-000000000204");
    public static readonly Guid XyzManager = new("0192f000-0000-7000-8000-000000000211");
    public static readonly Guid XyzUser1 = new("0192f000-0000-7000-8000-000000000212");
    public static readonly Guid GfManager = new("0192f000-0000-7000-8000-000000000221");
    public static readonly Guid AbcDisabled = new("0192f000-0000-7000-8000-000000000229");

    public static readonly Guid Server01 = new("0192f000-0000-7000-8000-000000000301");
    public static readonly Guid Server02 = new("0192f000-0000-7000-8000-000000000302");
    public static readonly Guid Server03 = new("0192f000-0000-7000-8000-000000000303");
    public static readonly Guid Server04 = new("0192f000-0000-7000-8000-000000000304");
    public static readonly Guid Server05 = new("0192f000-0000-7000-8000-000000000305");
    public static readonly Guid Server06 = new("0192f000-0000-7000-8000-000000000306");
    public static readonly Guid Server07 = new("0192f000-0000-7000-8000-000000000307");
    public static readonly Guid Staging01 = new("0192f000-0000-7000-8000-000000000308");

    public static readonly Guid SharedPoolA = new("0192f000-0000-7000-8000-000000000401");
    public static readonly Guid FailoverPool = new("0192f000-0000-7000-8000-000000000402");
    public static readonly Guid StagingPool = new("0192f000-0000-7000-8000-000000000403");

    public static readonly Guid PolicySecondaryThenPool = new("0192f000-0000-7000-8000-000000000501");
    public static readonly Guid PolicyNoFailover = new("0192f000-0000-7000-8000-000000000502");
    public static readonly Guid PolicyPoolFailover = new("0192f000-0000-7000-8000-000000000503");
}

public sealed class DatabaseInitializer(
    WingsDbContext db,
    IPasswordHasher hasher,
    IOptions<SeedOptions> options,
    TimeProvider time,
    ILogger<DatabaseInitializer> logger)
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().RunAsync(ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (o.ApplyMigrations) await db.Database.MigrateAsync(ct);

        await SeedSecurityAsync(ct);
        await SeedSettingsAsync(ct);
        if (o.SampleData && !await db.Companies.AnyAsync(ct)) await SeedSampleAsync(ct);
        if (!await db.ConfigurationVersions.AnyAsync(ct))
        {
            db.ConfigurationVersions.Add(new ConfigurationVersion
            {
                Version = time.GetUtcNow().ToUnixTimeMilliseconds() * 1000, ChangedAt = time.GetUtcNow(),
                ChangedBy = "initializer", Summary = "Initial configuration"
            });
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SeedSecurityAsync(CancellationToken ct)
    {
        var existing = await db.Permissions.ToDictionaryAsync(p => p.Code, ct);
        foreach (var (code, description) in Permissions.Descriptions)
        {
            if (!existing.ContainsKey(code))
            {
                var p = new Permission { Code = code, Description = description };
                db.Permissions.Add(p);
                existing[code] = p;
            }
        }

        var roles = await db.Roles.Include(r => r.Permissions).ToDictionaryAsync(r => r.Name, ct);
        foreach (var (name, codes) in Permissions.BuiltInRoles)
        {
            if (!roles.TryGetValue(name, out var role))
            {
                role = new Role { Name = name, IsSystem = true, Description = $"Built-in {name} role" };
                db.Roles.Add(role);
                roles[name] = role;
            }
            foreach (var code in codes)
            {
                var permission = existing[code];
                if (role.Permissions.All(rp => rp.PermissionId != permission.Id))
                    role.Permissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            }
        }

        if (!await db.Administrators.AnyAsync(ct))
        {
            var password = options.Value.AdminPassword;
            if (string.IsNullOrWhiteSpace(password))
            {
                logger.LogWarning("No administrator exists and Seed:AdminPassword is not set; no administrator was created");
            }
            else
            {
                var admin = new Administrator
                {
                    Username = options.Value.AdminUsername,
                    NormalizedUsername = IdentifierNormalizer.Normalize(options.Value.AdminUsername),
                    DisplayName = "Administrator",
                    PasswordHash = hasher.Hash(password)
                };
                admin.Roles.Add(new AdministratorRole { AdministratorId = admin.Id, RoleId = roles["SuperAdministrator"].Id });
                db.Administrators.Add(admin);
                logger.LogInformation("Created initial administrator '{Username}'", admin.Username);
            }
        }

        if (!await db.RoutingPolicies.AnyAsync(ct))
            db.RoutingPolicies.Add(new RoutingPolicy { Name = "Default", IsDefault = true });

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSettingsAsync(CancellationToken ct)
    {
        var keys = await db.SystemSettings.Select(s => s.Key).ToListAsync(ct);
        foreach (var (key, (value, description)) in SettingKeys.Defaults)
        {
            if (!keys.Contains(key))
                db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Description = description, ModifiedAt = time.GetUtcNow() });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSampleAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var since = now.AddDays(-1);
        var userPassword = o.SampleUserPassword
            ?? throw new InvalidOperationException("Seed:SampleUserPassword is required when Seed:SampleData is true");
        var passwordHash = hasher.Hash(userPassword);
        string Url(int port, string name) =>
            o.SampleBackendUrlTemplate.Replace("{port}", port.ToString()).Replace("{name}", name.ToLowerInvariant());

        var erp = new Application { Id = SampleIds.ErpApp, Code = "ERP", Name = "Wings ERP", Description = "Main ERP application" };
        var hr = new Application { Id = SampleIds.HrApp, Code = "HR", Name = "Wings HR", Description = "HR and payroll", MinimumServerVersion = "2.0" };
        erp.Environments.Add(new ApplicationEnvironment { ApplicationId = erp.Id, Environment = DeploymentEnvironment.Production, PublicHost = o.SampleProductionHost, PathPrefix = "/" });
        erp.Environments.Add(new ApplicationEnvironment { ApplicationId = erp.Id, Environment = DeploymentEnvironment.Staging, PublicHost = o.SampleStagingHost, PathPrefix = "/" });
        hr.Environments.Add(new ApplicationEnvironment { ApplicationId = hr.Id, Environment = DeploymentEnvironment.Production, PublicHost = o.SampleProductionHost, PathPrefix = "/hr" });
        db.Applications.AddRange(erp, hr);

        Server NewServer(Guid id, string name, int port, ServerType type, DeploymentEnvironment env = DeploymentEnvironment.Production,
            string region = "India-South", string version = "2.1.0")
        {
            var server = new Server
            {
                Id = id, Name = name, Hostname = $"{name.ToLowerInvariant()}.wings.internal", BaseUrl = Url(port, name),
                HealthCheckUrl = Url(port, name) + "/health/ready", Environment = env, Region = region, ServerType = type,
                CpuCores = 8, RamGb = 32, MaxCapacity = 500, ApplicationVersion = version, AdminState = ServerAdminState.Active,
                Weight = 100, Notes = "Sample server"
            };
            server.Applications.Add(new ServerApplication { ServerId = id, ApplicationId = erp.Id });
            server.Applications.Add(new ServerApplication { ServerId = id, ApplicationId = hr.Id });
            return server;
        }

        var s1 = NewServer(SampleIds.Server01, "APP-SERVER-01", 5101, ServerType.Dedicated);
        var s2 = NewServer(SampleIds.Server02, "APP-SERVER-02", 5102, ServerType.Dedicated);
        var s3 = NewServer(SampleIds.Server03, "APP-SERVER-03", 5103, ServerType.Shared);
        var s4 = NewServer(SampleIds.Server04, "APP-SERVER-04", 5104, ServerType.Shared);
        var s5 = NewServer(SampleIds.Server05, "APP-SERVER-05", 5105, ServerType.Shared);
        var s6 = NewServer(SampleIds.Server06, "APP-SERVER-06", 5106, ServerType.Hybrid);
        var s7 = NewServer(SampleIds.Server07, "APP-SERVER-07", 5107, ServerType.Dedicated, region: "India-West");
        var st = NewServer(SampleIds.Staging01, "STAGING-01", 5108, ServerType.Shared, DeploymentEnvironment.Staging, version: "2.2.0-rc1");
        s4.Weight = 200;
        db.Servers.AddRange(s1, s2, s3, s4, s5, s6, s7, st);

        var poolA = new ServerPool
        {
            Id = SampleIds.SharedPoolA, Name = "Shared Pool A", Description = "Shared pool for standard-plan companies",
            Environment = DeploymentEnvironment.Production, Region = "India-South",
            Algorithm = LoadBalancingAlgorithm.WeightedLeastRequests, Affinity = AffinityMode.PerCompany
        };
        poolA.Members.AddRange([
            new ServerPoolMember { PoolId = poolA.Id, ServerId = s3.Id, Priority = 1, AddedAt = now },
            new ServerPoolMember { PoolId = poolA.Id, ServerId = s4.Id, Priority = 2, AddedAt = now },
            new ServerPoolMember { PoolId = poolA.Id, ServerId = s5.Id, Priority = 3, AddedAt = now }]);
        var failoverPool = new ServerPool
        {
            Id = SampleIds.FailoverPool, Name = "Dedicated Failover Pool", Description = "Approved standby capacity for dedicated tenants",
            Environment = DeploymentEnvironment.Production, Algorithm = LoadBalancingAlgorithm.LowestUtilization, Affinity = AffinityMode.PerSession
        };
        failoverPool.Members.Add(new ServerPoolMember { PoolId = failoverPool.Id, ServerId = s6.Id, Priority = 1, AddedAt = now });
        var stagingPool = new ServerPool
        {
            Id = SampleIds.StagingPool, Name = "Staging Pool", Environment = DeploymentEnvironment.Staging,
            Algorithm = LoadBalancingAlgorithm.RoundRobin, Affinity = AffinityMode.PerSession
        };
        stagingPool.Members.Add(new ServerPoolMember { PoolId = stagingPool.Id, ServerId = st.Id, Priority = 1, AddedAt = now });
        db.ServerPools.AddRange(poolA, failoverPool, stagingPool);

        db.FailoverPolicies.AddRange(
            new FailoverPolicy
            {
                Id = SampleIds.PolicySecondaryThenPool, Name = "Secondary, then dedicated failover pool", FailoverEnabled = true,
                FailoverPoolId = failoverPool.Id, FailbackPolicy = FailbackPolicy.Automatic, MaxFailoverAttempts = 3
            },
            new FailoverPolicy { Id = SampleIds.PolicyNoFailover, Name = "No failover", FailoverEnabled = false, FailbackPolicy = FailbackPolicy.Manual, MaxFailoverAttempts = 0 },
            new FailoverPolicy
            {
                Id = SampleIds.PolicyPoolFailover, Name = "Pool members only", FailoverEnabled = true, FailoverPoolId = poolA.Id,
                FailbackPolicy = FailbackPolicy.EndOfSession, MaxFailoverAttempts = 2
            });

        Company NewCompany(Guid id, string code, string name, string plan, CompanyRoutingMode mode, params string[] aliases)
        {
            var c = new Company
            {
                Id = id, Code = code, NormalizedCode = IdentifierNormalizer.Normalize(code), Name = name, ServicePlan = plan,
                RoutingMode = mode, MaxConcurrentUsers = 200
            };
            foreach (var alias in aliases.Append(name))
                c.Aliases.Add(new CompanyAlias { CompanyId = id, Alias = alias, NormalizedAlias = IdentifierNormalizer.Normalize(alias) });
            return c;
        }

        var abc = NewCompany(SampleIds.AbcMotors, "ABC", "ABC Motors", "Enterprise", CompanyRoutingMode.Dedicated, "ABC-MOTORS");
        var xyz = NewCompany(SampleIds.XyzMotors, "XYZ", "XYZ Motors", "Standard", CompanyRoutingMode.SharedPool, "XYZ-AUTO");
        var gf = NewCompany(SampleIds.GlobalFreight, "GFL", "Global Freight Logistics", "Enterprise", CompanyRoutingMode.Dedicated);
        db.Companies.AddRange(abc, xyz, gf);

        void AddUser(Guid id, Company company, string username, string display, bool disabled = false)
        {
            var user = new User { Id = id, DisplayName = display, Email = $"{username}@{company.Code.ToLowerInvariant()}.example", PasswordHash = passwordHash };
            user.Memberships.Add(new UserCompanyMembership
            {
                UserId = id, CompanyId = company.Id, Username = username, NormalizedUsername = IdentifierNormalizer.Normalize(username),
                Status = disabled ? AccountStatus.Disabled : AccountStatus.Active, CreatedAt = now
            });
            db.Users.Add(user);
        }

        AddUser(SampleIds.AbcUser1, abc, "user1", "ABC User One");
        AddUser(SampleIds.AbcUser2, abc, "user2", "ABC User Two");
        AddUser(SampleIds.AbcManager, abc, "manager", "ABC Manager");
        AddUser(SampleIds.AbcAccounts, abc, "accounts", "ABC Accounts");
        AddUser(SampleIds.AbcDisabled, abc, "former.employee", "Former Employee", disabled: true);
        AddUser(SampleIds.XyzManager, xyz, "manager", "XYZ Manager");
        AddUser(SampleIds.XyzUser1, xyz, "user1", "XYZ User One");
        AddUser(SampleIds.GfManager, gf, "manager", "GFL Manager");

        // Scenario A: ABC user1 → APP-SERVER-01 (user dedicated). accounts → APP-SERVER-01 too.
        db.UserServerAssignments.AddRange(
            new UserServerAssignment
            {
                CompanyId = abc.Id, UserId = SampleIds.AbcUser1, Environment = DeploymentEnvironment.Production, PrimaryServerId = s1.Id,
                FailoverPolicyId = SampleIds.PolicyNoFailover, EffectiveFrom = since, CreatedAt = now, CreatedBy = "seed",
                Reason = "Sample: user-dedicated server"
            },
            new UserServerAssignment
            {
                CompanyId = abc.Id, UserId = SampleIds.AbcAccounts, Environment = DeploymentEnvironment.Production, PrimaryServerId = s1.Id,
                SecondaryServerId = s2.Id, FailoverPolicyId = SampleIds.PolicySecondaryThenPool, EffectiveFrom = since, CreatedAt = now,
                CreatedBy = "seed", Reason = "Sample: accounts user on server 01 with failover to the company server"
            });
        // Scenario B: other ABC users → APP-SERVER-02, secondary APP-SERVER-06 when failover is needed.
        db.CompanyServerAssignments.AddRange(
            new CompanyServerAssignment
            {
                CompanyId = abc.Id, Environment = DeploymentEnvironment.Production, PrimaryServerId = s2.Id, SecondaryServerId = s6.Id,
                FailoverPolicyId = SampleIds.PolicySecondaryThenPool, EffectiveFrom = since, CreatedAt = now, CreatedBy = "seed",
                Reason = "Sample: company-dedicated server"
            },
            new CompanyServerAssignment
            {
                CompanyId = gf.Id, Environment = DeploymentEnvironment.Production, PrimaryServerId = s7.Id,
                FailoverPolicyId = SampleIds.PolicyNoFailover, EffectiveFrom = since, CreatedAt = now, CreatedBy = "seed",
                Reason = "Sample: dedicated tenant without failover"
            });
        // Scenario C: XYZ → Shared Pool A (production) and the staging pool.
        db.SharedPoolAssignments.AddRange(
            new SharedPoolAssignment
            {
                CompanyId = xyz.Id, PoolId = poolA.Id, Environment = DeploymentEnvironment.Production, EffectiveFrom = since,
                CreatedAt = now, CreatedBy = "seed", Reason = "Sample: shared pool"
            },
            new SharedPoolAssignment
            {
                CompanyId = xyz.Id, PoolId = stagingPool.Id, Environment = DeploymentEnvironment.Staging, EffectiveFrom = since,
                CreatedAt = now, CreatedBy = "seed", Reason = "Sample: staging"
            },
            new SharedPoolAssignment
            {
                CompanyId = abc.Id, PoolId = stagingPool.Id, Environment = DeploymentEnvironment.Staging, EffectiveFrom = since,
                CreatedAt = now, CreatedBy = "seed", Reason = "Sample: staging"
            });
        db.CapacityReservations.Add(new CapacityReservation { ServerId = s6.Id, CompanyId = abc.Id, ReservedSessions = 100 });
        // APP-SERVER-01 holds ABC data for its user-dedicated assignments.
        db.CompanyServerAuthorizations.Add(new CompanyServerAuthorization
        {
            CompanyId = abc.Id, ServerId = s1.Id, GrantedAt = now, GrantedBy = "seed", Notes = "ABC user-dedicated server"
        });

        db.NotificationChannels.Add(new NotificationChannel
        {
            Name = "Operations e-mail (sample, disabled)", Type = NotificationChannelType.Email, Target = "ops@example.com",
            MinimumSeverity = AlertSeverity.Warning, IsEnabled = false
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Sample data created: 3 companies, 8 users, 8 servers, 3 pools");
    }
}
