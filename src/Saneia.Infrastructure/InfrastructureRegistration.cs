using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Saneia.Application.Monitoring;
using Saneia.Infrastructure.Catalog;
using Saneia.Infrastructure.Health;
using Saneia.Infrastructure.Persistence;

namespace Saneia.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddSaneiaInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var catalogUrl = config["Java:BaseUrl"] ?? throw new InvalidOperationException("Configure Java:BaseUrl.");
        var baseAddress = new Uri(catalogUrl.TrimEnd('/') + "/");
        services.AddHttpClient<JavaCatalog>(http =>
        {
            http.BaseAddress = baseAddress;
            http.Timeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient("java-health", http =>
        {
            http.BaseAddress = baseAddress;
            http.Timeout = TimeSpan.FromSeconds(3);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<ISensorCatalog>(sp => sp.GetRequiredService<JavaCatalog>());

        if (!UsesSqlite(config) && !string.Equals(config["Database:Provider"], "Oracle", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Database:Provider deve ser Oracle ou Sqlite.");

        services.AddDbContext<TelemetryDb>(options =>
        {
            if (UsesSqlite(config))
                options.UseSqlite(config.GetConnectionString("Sqlite") ?? "Data Source=saneia.local.db");
            else
                options.UseOracle(config.GetConnectionString("Oracle")
                    ?? throw new InvalidOperationException("Configure ConnectionStrings:Oracle."));
        });
        services.AddScoped<ITelemetryStore, EfTelemetryStore>();
        services.AddHealthChecks()
            .AddCheck<TelemetryHealth>("telemetry_db", tags: ["ready"])
            .AddCheck<JavaHealth>("java_catalog", tags: ["ready"]);
        return services;
    }

    public static async Task<string?> PrepareTelemetryDatabase(this IServiceProvider services, IConfiguration config)
    {
        var printSchema = config.GetValue<bool>("Database:PrintSchema");
        if (!printSchema && !UsesSqlite(config)) return null;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDb>();
        if (printSchema) return db.Database.GenerateCreateScript();

        await db.Database.EnsureCreatedAsync();
        return null;
    }

    private static bool UsesSqlite(IConfiguration config) =>
        string.Equals(config["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase);
}
