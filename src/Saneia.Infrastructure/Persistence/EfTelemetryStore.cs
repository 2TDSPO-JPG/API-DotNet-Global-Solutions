using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
using Saneia.Application.Monitoring;
using Saneia.Domain.Telemetry;

namespace Saneia.Infrastructure.Persistence;

internal sealed class EfTelemetryStore(TelemetryDb db) : ITelemetryStore
{
    public async Task<IReadOnlyDictionary<string, SensorSnapshot>> ReadMap(
        string[] ids, long alarmsFrom, CancellationToken cancellation)
    {
        if (ids.Length == 0) return new Dictionary<string, SensorSnapshot>();

        var latest = await (from row in db.Measurements.AsNoTracking()
            join cursor in db.Cursors.AsNoTracking() on new { row.SensorId, row.AtTicks }
                equals new { cursor.SensorId, AtTicks = cursor.LastTicks }
            where ids.Contains(row.SensorId)
            select new { Reading = row, cursor.ActiveAlarmId }).ToArrayAsync(cancellation);
        var alarms = await db.Events.AsNoTracking()
            .Where(row => ids.Contains(row.SensorId) && row.StartTicks >= alarmsFrom)
            .GroupBy(row => row.SensorId)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Id, row => row.Count, cancellation);
        return latest.ToDictionary(row => row.Reading.SensorId, row => new SensorSnapshot(
            row.ActiveAlarmId is not null, row.Reading, [], [], alarms.GetValueOrDefault(row.Reading.SensorId)));
    }

    public async Task<SensorSnapshot> ReadDetail(string id, long alarmsFrom, CancellationToken cancellation)
    {
        var cursor = await FindCursor(id, cancellation);
        if (cursor is null) return SensorSnapshot.Empty;

        // Use the captured timestamp for both queries if ingestion advances concurrently.
        var asOf = cursor.LastTicks;
        var readings = await db.Measurements.AsNoTracking()
            .Where(row => row.SensorId == id && row.AtTicks <= asOf)
            .OrderByDescending(row => row.AtTicks).Take(500).ToArrayAsync(cancellation);
        Array.Reverse(readings);
        var events = await db.Events.AsNoTracking()
            .Where(row => row.SensorId == id && row.StartTicks <= asOf)
            .OrderByDescending(row => row.StartTicks).Take(100).ToArrayAsync(cancellation);
        foreach (var alarm in events)
            if (alarm.EndTicks > asOf) alarm.EndTicks = null;

        var count = await db.Events.CountAsync(row => row.SensorId == id &&
            row.StartTicks >= alarmsFrom && row.StartTicks <= asOf, cancellation);
        return new(cursor.ActiveAlarmId is not null, readings.LastOrDefault(), readings, events, count);
    }

    public Task<Measurement?> Find(string deviceId, string sampleId, CancellationToken cancellation) =>
        db.Measurements.AsNoTracking().SingleOrDefaultAsync(row =>
            row.DeviceId == deviceId && row.SampleId == sampleId, cancellation);

    public async Task<TelemetrySummary> Summarize(string id, DateTimeOffset from, DateTimeOffset to,
        DateTimeOffset now, int intervalMinutes, decimal limit, CancellationToken cancellation)
    {
        var cursor = await FindCursor(id, cancellation);
        var latestTicks = cursor?.LastTicks ?? 0;
        var lastAt = cursor is null ? (DateTimeOffset?)null : new(latestTicks, TimeSpan.Zero);
        var start = from.UtcTicks;
        var end = to.UtcTicks;
        var upperReading = Math.Min(end, latestTicks);
        var sensorReadings = db.Measurements.AsNoTracking().Where(row => row.SensorId == id);
        var period = sensorReadings.Where(row => row.AtTicks >= start && row.AtTicks <= upperReading);
        var totals = await period.GroupBy(row => row.SensorId).Select(group => new
        {
            Count = group.Count(),
            Min = group.Min(row => (double)row.Volume),
            Average = group.Average(row => (double)row.Volume),
            Above = group.Count(row => row.Status == MonitoringRules.Alarm)
        }).SingleOrDefaultAsync(cancellation);
        var first = await period.OrderBy(row => row.AtTicks).FirstOrDefaultAsync(cancellation);
        var recent = await period.OrderByDescending(row => row.AtTicks).Take(2).ToArrayAsync(cancellation);
        var last = recent.FirstOrDefault();
        var peak = await period.OrderByDescending(row => (double)row.Volume).ThenBy(row => row.AtTicks)
            .FirstOrDefaultAsync(cancellation);
        var alarms = await db.Events.AsNoTracking()
            .Where(row => row.SensorId == id && row.StartTicks <= upperReading
                && (row.EndTicks == null || row.EndTicks >= start))
            .GroupBy(row => row.SensorId).Select(group => new
            {
                Count = group.Count(),
                Longest = group.Max(row =>
                    (row.EndTicks == null || row.EndTicks > latestTicks || row.EndTicks > end ? end : row.EndTicks.Value)
                    - (row.StartTicks < start ? start : row.StartTicks))
            }).SingleOrDefaultAsync(cancellation);
        var latest = cursor is null ? null : await sensorReadings
            .SingleOrDefaultAsync(row => row.AtTicks == latestTicks, cancellation);
        var offline = MonitoringRules.IsOffline(lastAt, now, intervalMinutes);
        var status = offline ? MonitoringRules.Offline
            : cursor?.ActiveAlarmId is not null ? MonitoringRules.Alarm
            : latest?.Status ?? MonitoringRules.Offline;
        var eta = !offline && recent.Length == 2 ? MonitoringRules.EstimateHours(
            new DateTimeOffset(recent[1].AtTicks, TimeSpan.Zero), recent[1].Volume,
            new DateTimeOffset(recent[0].AtTicks, TimeSpan.Zero), recent[0].Volume, limit) : null;

        return new(totals?.Count ?? 0, (double?)first?.Volume, (double?)last?.Volume, totals?.Min,
            (double?)peak?.Volume, peak is null ? null : new DateTimeOffset(peak.AtTicks, TimeSpan.Zero),
            totals?.Average, (double?)(last?.Volume - first?.Volume), totals?.Above ?? 0, alarms?.Count ?? 0,
            alarms is null ? null : TimeSpan.FromTicks(alarms.Longest).TotalHours, eta, status, lastAt);
    }

    public Task<SensorCursor?> FindCursor(string sensorId, CancellationToken cancellation) =>
        db.Cursors.AsNoTracking().SingleOrDefaultAsync(row => row.SensorId == sensorId, cancellation);

    public async Task<bool> TryAppend(Measurement reading, SensorCursor cursor, long? previousVersion,
        AlarmChange alarm, CancellationToken cancellation)
    {
        db.Measurements.Add(reading);
        if (previousVersion is { } version)
        {
            var entry = db.Cursors.Update(cursor);
            entry.Property(row => row.Version).OriginalValue = version;
        }
        else
        {
            db.Cursors.Add(cursor);
        }

        if (alarm.Opened is { } opened)
            db.Events.Add(opened);
        if (alarm.ClosedId is { } closed)
        {
            var openAlarm = await db.Events.SingleAsync(row => row.Id == closed, cancellation);
            openAlarm.EndTicks = reading.AtTicks;
        }

        try
        {
            // EF writes the cursor, sample and event in a single transaction.
            await db.SaveChangesAsync(cancellation);
            return true;
        }
        catch (DbUpdateException error) when (IsWriteConflict(error))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static bool IsWriteConflict(DbUpdateException error) =>
        error is DbUpdateConcurrencyException ||
        error.InnerException is SqliteException { SqliteErrorCode: 19 } ||
        error.InnerException is OracleException { Number: 1 or 8177 };
}
