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
