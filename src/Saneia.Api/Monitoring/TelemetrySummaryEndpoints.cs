using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Saneia.Application.Monitoring;

namespace Saneia.Api.Monitoring;

internal static class TelemetrySummaryEndpoints
{
    public static void MapTelemetrySummary(this WebApplication app, IConfiguration config)
    {
        var key = Encoding.UTF8.GetBytes(config["SANEIA_INTEGRATION_KEY"] ?? "");
        app.MapGet("/api/internal/telemetry/sensors/{id:long}/summary", (
            long id, DateTimeOffset from, DateTimeOffset to, int intervalMinutes, decimal limit,
            ITelemetryStore store, TimeProvider clock, CancellationToken cancellation) =>
                Summarize(id, from, to, intervalMinutes, limit, store, clock, cancellation))
            .AddEndpointFilter(async (context, next) =>
            {
                var supplied = context.HttpContext.Request.Headers["X-Saneia-Integration-Key"];
                return key.Length >= 32 && supplied.Count == 1
                    && CryptographicOperations.FixedTimeEquals(key, Encoding.UTF8.GetBytes(supplied.ToString()))
                    ? await next(context) : Results.Unauthorized();
            })
            .ExcludeFromDescription();
    }

    private static async Task<IResult> Summarize(long id, DateTimeOffset from, DateTimeOffset to,
        int intervalMinutes, decimal limit, ITelemetryStore store, TimeProvider clock, CancellationToken cancellation)
    {
        var now = clock.GetUtcNow();
        if (id <= 0 || from < DateTimeOffset.UnixEpoch || to < from || to > now.AddMinutes(1)
            || intervalMinutes is < 1 or > 1440 || limit <= 0)
            return Results.BadRequest(new ApiError("Informe equipamento, período, intervalo e limite válidos."));

        return Results.Ok(await store.Summarize(id.ToString(CultureInfo.InvariantCulture), from,
            to < now ? to : now, now, intervalMinutes, limit, cancellation));
    }
}
