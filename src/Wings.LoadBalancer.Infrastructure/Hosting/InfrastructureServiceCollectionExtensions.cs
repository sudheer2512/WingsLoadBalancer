using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Infrastructure.Alerts;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.LoadBalancer.Infrastructure.Authentication;
using Wings.LoadBalancer.Infrastructure.Caching;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Health;
using Wings.LoadBalancer.Infrastructure.Monitoring;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.LoadBalancer.Infrastructure.Sessions;
using Wings.Routing.Balancing;
using Wings.Routing.Engine;
using Wings.Routing.Runtime;

namespace Wings.LoadBalancer.Infrastructure.Hosting;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Services shared by the gateway, admin API and monitor.</summary>
    public static IServiceCollection AddWingsInfrastructure(this IServiceCollection services, IConfiguration configuration,
        string applicationName)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddWingsData(configuration);

        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.Section));
        services.Configure<ConfigurationSyncOptions>(configuration.GetSection(ConfigurationSyncOptions.Section));
        services.Configure<HealthOptions>(configuration.GetSection(HealthOptions.Section));
        services.Configure<BackendConnectionOptions>(configuration.GetSection(BackendConnectionOptions.Section));
        services.Configure<BackendTrustOptions>(configuration.GetSection(BackendTrustOptions.Section));
        services.Configure<DestinationPolicyOptions>(configuration.GetSection(DestinationPolicyOptions.Section));
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.Section));
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));
        services.Configure<AuthenticationOptions>(configuration.GetSection(AuthenticationOptions.Section));
        services.Configure<WingsAuthServiceOptions>(configuration.GetSection(WingsAuthServiceOptions.Section));
        services.Configure<OperationsOptions>(configuration.GetSection(OperationsOptions.Section));

        var redis = configuration.GetSection(RedisOptions.Section).Get<RedisOptions>() ?? new RedisOptions();
        if (string.IsNullOrWhiteSpace(redis.Configuration))
            services.TryAddSingleton<IStateStore, InMemoryStateStore>();
        else
            services.TryAddSingleton<IStateStore, RedisStateStore>();

        // Data Protection keys must be shared by every gateway instance so any of them can read session cookies.
        // Default: the routing database (survives Redis outages). Alternatives: a file share or Redis.
        var dp = services.AddDataProtection().SetApplicationName("Wings.LoadBalancer");
        var keyPath = configuration["DataProtection:KeyPath"];
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            dp.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        }
        else if (configuration.GetValue("DataProtection:UseRedis", false) && !string.IsNullOrWhiteSpace(redis.Configuration))
        {
            var lazy = new Lazy<IConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(redis.Configuration!));
            dp.PersistKeysToStackExchangeRedis(() => lazy.Value.GetDatabase(), $"{redis.KeyPrefix}dataprotection-keys");
        }
        else
        {
            dp.PersistKeysToDbContext<WingsDbContext>();
        }
        if (OperatingSystem.IsWindows() && configuration.GetValue("DataProtection:ProtectWithDpapiMachine", false))
            dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
        else if (configuration["DataProtection:CertificateThumbprint"] is { Length: > 0 } thumbprint)
            dp.ProtectKeysWithCertificate(thumbprint);

        services.TryAddSingleton<IPasswordHasher>(sp => new Pbkdf2PasswordHasher(configuration.GetValue("Authentication:PasswordIterations", 210_000)));
        services.TryAddSingleton<ITotpService, TotpService>();
        services.TryAddSingleton<IBackendKeyProvider, BackendKeyProvider>();
        services.TryAddSingleton<IDestinationValidator, DestinationValidator>();
        services.TryAddSingleton<IBackendHttpClientFactory, BackendHttpClientFactory>();
        services.TryAddSingleton<IHealthProbe, HealthProbe>();
        services.TryAddSingleton<ILeaseService, DbLeaseService>();
        services.TryAddSingleton<ILoadBalancingStrategyRegistry>(_ => new LoadBalancingStrategyRegistry());
        services.TryAddSingleton(sp => new RoutingEngineOptions
        {
            LastKnownGoodMaxAge = configuration.GetValue<int?>("Routing:LastKnownGoodMaxAgeMinutes", 30) is { } minutes && minutes > 0
                ? TimeSpan.FromMinutes(minutes)
                : null,
            TreatUnknownHealthAsHealthy = configuration.GetValue("Routing:TreatUnknownHealthAsHealthy", false)
        });
        services.TryAddSingleton<IRoutingEngine, RoutingEngine>();

        // Scoped: it records the identity of the current request/unit of work (IAuditContext).
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<ISystemSettings, SystemSettingsService>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<IUserAuthenticator, UserAuthenticator>();
        services.AddScoped<IExternalCredentialValidator, WingsAuthServiceValidator>();
        services.AddScoped<DatabaseInitializer>();
        services.AddSingleton<IConfigurationChangeNotifier, StateStoreConfigurationChangeNotifier>();

        services.AddHttpClient(NotificationDispatcher.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => NotificationDispatcher.CreateHandler(sp.GetRequiredService<IOptions<NotificationOptions>>().Value));
        services.AddHttpClient(WingsAuthServiceValidator.HttpClientName);
        return services;
    }

    /// <summary>Gateway runtime: snapshot sync, runtime state, health feed or local probes, background writers.</summary>
    public static IServiceCollection AddWingsGatewayRuntime(this IServiceCollection services)
    {
        services.AddSingleton<RoutingSnapshotService>();
        services.AddSingleton<IRoutingSnapshotProvider>(sp => sp.GetRequiredService<RoutingSnapshotService>());
        services.AddHostedService(sp => sp.GetRequiredService<RoutingSnapshotService>());

        services.AddSingleton<ServerStateStore>();
        services.AddSingleton<IServerStateView>(sp => sp.GetRequiredService<ServerStateStore>());
        services.AddSingleton<PassiveHealthPublisher>();
        services.AddHostedService<SharedHealthFeedService>();
        services.AddHostedService<LocalHealthProbeService>();

        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<DecisionLogWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<DecisionLogWriter>());
        services.AddSingleton<SessionActivityWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionActivityWriter>());
        return services;
    }

    /// <summary>Monitor runtime: leader-elected health monitor and operations (drain, maintenance, alerts, retention).</summary>
    public static IServiceCollection AddWingsMonitorRuntime(this IServiceCollection services)
    {
        services.AddSingleton<HealthMonitorService>();
        services.AddHostedService(sp => sp.GetRequiredService<HealthMonitorService>());
        services.AddSingleton<OperationsService>();
        services.AddHostedService(sp => sp.GetRequiredService<OperationsService>());
        return services;
    }
}
