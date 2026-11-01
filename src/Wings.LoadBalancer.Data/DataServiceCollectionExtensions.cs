using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Wings.LoadBalancer.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WingsDbContext"/> for the configured provider (<c>Database:Provider</c> = SqlServer|Sqlite)
    /// using connection string <c>ConnectionStrings:Wings</c>.
    /// </summary>
    public static IServiceCollection AddWingsData(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connectionString = configuration.GetConnectionString("Wings")
            ?? (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) ? "Data Source=App_Data/wings.db" : null)
            ?? throw new InvalidOperationException("ConnectionStrings:Wings is required");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAuditContext>(_ => new SystemAuditContext());
        services.AddScoped<WingsSaveChangesInterceptor>();

        if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContext<SqlServerWingsDbContext>((sp, o) => o
                .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3).CommandTimeout(30))
                .AddInterceptors(sp.GetRequiredService<WingsSaveChangesInterceptor>()));
            services.AddScoped<WingsDbContext>(sp => sp.GetRequiredService<SqlServerWingsDbContext>());
        }
        else
        {
            EnsureSqliteDirectory(connectionString);
            services.AddDbContext<SqliteWingsDbContext>((sp, o) => o
                .UseSqlite(connectionString)
                .AddInterceptors(sp.GetRequiredService<WingsSaveChangesInterceptor>()));
            services.AddScoped<WingsDbContext>(sp => sp.GetRequiredService<SqliteWingsDbContext>());
        }

        services.AddScoped<IRoutingConfigurationSource, DbRoutingConfigurationSource>();
        return services;
    }

    private static void EnsureSqliteDirectory(string connectionString)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrEmpty(directory) && builder.DataSource != ":memory:") Directory.CreateDirectory(directory);
    }
}

/// <summary>Design-time factories so <c>dotnet ef</c> can build each provider's migrations.</summary>
public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerWingsDbContext>
{
    public SqlServerWingsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerWingsDbContext>()
            .UseSqlServer("Server=localhost;Database=WingsLoadBalancer;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}

public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteWingsDbContext>
{
    public SqliteWingsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteWingsDbContext>().UseSqlite("Data Source=design.db").Options);
}
