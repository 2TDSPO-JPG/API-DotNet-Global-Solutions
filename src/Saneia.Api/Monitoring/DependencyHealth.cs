using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Saneia.Api.Data;

namespace Saneia.Api.Monitoring;

public sealed class TelemetryHealth(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDb>();
        await db.Measurements.AsNoTracking().AnyAsync(cancellationToken);
        await db.Cursors.AsNoTracking().AnyAsync(cancellationToken);
        await db.Events.AsNoTracking().AnyAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}

public sealed class JavaHealth(IHttpClientFactory clients) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        using var http = clients.CreateClient("java-health");
        using var response = await http.GetAsync("actuator/health", cancellationToken);
        return response.IsSuccessStatusCode ? HealthCheckResult.Healthy() :
            HealthCheckResult.Unhealthy("Catálogo Java indisponível.");
    }
}
