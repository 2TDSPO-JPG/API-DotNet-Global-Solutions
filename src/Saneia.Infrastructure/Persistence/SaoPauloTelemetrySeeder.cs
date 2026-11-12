using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Saneia.Application.Monitoring;
using Saneia.Domain.Telemetry;
using Saneia.Infrastructure.Catalog;

namespace Saneia.Infrastructure.Persistence;

internal sealed class SaoPauloTelemetrySeeder(IServiceScopeFactory scopes, IConfiguration config,
    TimeProvider clock, ILogger<SaoPauloTelemetrySeeder> log) : BackgroundService
{
    private const string Source = "Simulação SP";
    private static readonly int[] HistoricalDays = [120, 90, 60, 30, 14, 7, 2, 1];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var ready = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var catalog = scope.ServiceProvider.GetRequiredService<JavaCatalog>();
                var sites = await catalog.ListDemoDevices(config["SANEIA_INTEGRATION_KEY"]!, stoppingToken);
                var db = scope.ServiceProvider.GetRequiredService<TelemetryDb>();
                ready = await Seed(db, sites, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning("Carga de simulação SP indisponível ({Error}).", error.GetType().Name);
            }
            await Task.Delay(ready ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(12), stoppingToken);
        }
    }

    private async Task<bool> Seed(TelemetryDb db, DeviceCatalog[] sites, CancellationToken cancellation)
    {
        if (sites.Length == 0) return false;
        var ids = sites.Select(site => site.Id).ToArray();
        var cursors = await db.Cursors.Where(row => ids.Contains(row.SensorId))
            .ToDictionaryAsync(row => row.SensorId, cancellation);
        var latest = await (from row in db.Measurements.AsNoTracking()
            join cursor in db.Cursors.AsNoTracking()
                on new { row.SensorId, row.AtTicks } equals new { cursor.SensorId, AtTicks = cursor.LastTicks }
            where ids.Contains(row.SensorId) && row.Source == Source
            select row).ToDictionaryAsync(row => row.SensorId, cancellation);
        var now = clock.GetUtcNow();
        var created = 0;
        foreach (var site in sites)
        {
            if (site.Prefix.Length != 7 || !site.Prefix.StartsWith("SP-D", StringComparison.Ordinal)
                || !int.TryParse(site.Prefix.AsSpan(4), out var sequence) || site.Capacity <= 0
                || site.Limit <= 0 || site.Limit > site.Capacity)
                continue;
            var scenario = sequence % 50;
            var offline = scenario >= 47;
            if (cursors.TryGetValue(site.Id, out var existing))
            {
                if (!offline && latest.TryGetValue(site.Id, out var prior)
                    && now.UtcTicks - existing.LastTicks >= TimeSpan.FromMinutes(10).Ticks)
                    await AddReading(db, site, existing, now, Math.Min(prior.Volume, site.Capacity), cancellation);
                continue;
            }
            var cursor = new SensorCursor { SensorId = site.Id };
            db.Cursors.Add(cursor);
            foreach (var day in HistoricalDays)
            {
                var at = now.AddDays(-day);
                if (sequence % 5 == 0 && day is 60 or 14)
                {
                    await AddReading(db, site, cursor, at, Volume(site, 1.035m), cancellation);
                    await AddReading(db, site, cursor, at.AddMinutes(20), Volume(site, 0.73m), cancellation);
                    await AddReading(db, site, cursor, at.AddMinutes(40), Volume(site, 0.65m), cancellation);
                }
                else
                {
                    await AddReading(db, site, cursor, at, Volume(site, 0.42m + (sequence + day) % 29 / 100m),
                        cancellation);
                }
            }
            var end = now.AddMinutes(offline ? -160 : -1);
            var baseline = 0.52m + sequence % 20 / 100m;
            var trend = scenario switch
            {
                < 35 => new[] { baseline - 0.08m, baseline - 0.02m, baseline + 0.03m, baseline },
                < 43 => [0.67m, 0.75m, 0.84m, 0.88m + sequence % 8 / 100m],
                < 47 => [0.81m, 0.92m, 1.015m, 1.03m + sequence % 4 / 100m],
                _ => [0.49m, 0.55m, 0.62m, 0.64m],
            };
            for (var index = 0; index < trend.Length; index++)
                await AddReading(db, site, cursor, end.AddMinutes((index - 3) * 10),
                    Volume(site, trend[index]), cancellation);
            created++;
        }
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellation);
        if (created > 0)
            log.LogInformation("Histórico simulado cadastrado para {Count} pontos de São Paulo.", created);
        return true;
    }

    private static decimal Volume(DeviceCatalog site, decimal fraction) =>
        Math.Min(site.Capacity, decimal.Round(site.Limit * fraction));

    private static async Task AddReading(TelemetryDb db, DeviceCatalog site, SensorCursor cursor,
        DateTimeOffset at, decimal volume, CancellationToken cancellation)
    {
        var reading = new Measurement
        {
            SensorId = site.Id, DeviceId = site.Prefix, SampleId = $"sp-demo-{at.UtcTicks}", AtTicks = at.UtcTicks,
            Volume = volume, LevelCm = decimal.Round(volume / site.Capacity * 200, 1),
            Capacity = site.Capacity, Limit = site.Limit, Status = MonitoringRules.ReadingStatus(volume, site.Limit),
            Source = Source,
        };
        var alarm = cursor.Apply(reading);
        db.Measurements.Add(reading);
        if (alarm.Opened is { } open) db.Events.Add(open);
        if (alarm.ClosedId is { } id && await db.Events.FindAsync([id], cancellation) is { } closed)
            closed.EndTicks = reading.AtTicks;
    }
}
