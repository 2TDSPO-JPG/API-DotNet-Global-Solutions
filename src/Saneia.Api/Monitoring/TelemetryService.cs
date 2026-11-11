using Microsoft.EntityFrameworkCore;
using Saneia.Api.Data;

namespace Saneia.Api.Monitoring;

public sealed class TelemetryService(TelemetryDb db, TimeProvider clock)
{
    public async Task<Sensor[]> Map(CatalogSensor[] catalog, CancellationToken cancellation)
    {
        if (catalog.Length == 0) return [];
        var ids = catalog.Select(sensor => sensor.Id).ToArray();
        var states = await db.Cursors.AsNoTracking().Where(row => ids.Contains(row.SensorId))
            .ToDictionaryAsync(row => row.SensorId, cancellation);
        var lastReadings = await (from row in db.Measurements.AsNoTracking()
            join state in db.Cursors on new { row.SensorId, row.AtTicks }
                equals new { state.SensorId, AtTicks = state.LastTicks }
            where ids.Contains(row.SensorId)
            select row).ToDictionaryAsync(row => row.SensorId, cancellation);
        var start = clock.GetUtcNow().AddMonths(-6).UtcTicks;
        var alarms = await db.Events.AsNoTracking()
            .Where(row => ids.Contains(row.SensorId) && row.StartTicks >= start)
            .GroupBy(row => row.SensorId)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Id, row => row.Count, cancellation);
        return catalog.Select(item => ToSensor(item, states.GetValueOrDefault(item.Id),
            lastReadings.GetValueOrDefault(item.Id), [], [], alarms.GetValueOrDefault(item.Id))).ToArray();
    }

    public async Task<Sensor> Detail(CatalogSensor item, CancellationToken cancellation)
    {
        var state = await db.Cursors.AsNoTracking().SingleOrDefaultAsync(row => row.SensorId == item.Id, cancellation);
        // Use the cursor's timestamp for a consistent view even if ingestion advances concurrently.
        var asOf = state?.LastTicks ?? 0;
        var readings = await db.Measurements.AsNoTracking()
            .Where(row => row.SensorId == item.Id && row.AtTicks <= asOf)
            .OrderByDescending(row => row.AtTicks).Take(500).ToArrayAsync(cancellation);
        Array.Reverse(readings);
        var samples = readings.Select(ToSample).ToArray();
        var recent = await db.Events.AsNoTracking()
            .Where(row => row.SensorId == item.Id && row.StartTicks <= asOf)
            .OrderByDescending(row => row.StartTicks).Take(100).ToArrayAsync(cancellation);
        var events = recent.Select(row => new SensorEvent(row.Id.ToString(), MonitoringRules.Alarm, "level_alarm",
            At(row.StartTicks), row.EndTicks is { } end && end <= asOf ? At(end) : null,
            "O volume atingiu o limite operacional.")).ToArray();
        var start = clock.GetUtcNow().AddMonths(-6).UtcTicks;
        var count = await db.Events.CountAsync(row => row.SensorId == item.Id &&
            row.StartTicks >= start && row.StartTicks <= asOf, cancellation);
        return ToSensor(item, state, readings.LastOrDefault(), samples, events, count);
    }

    public async Task<IResult> Ingest(string id, ReadingInput input, JavaCatalog catalog, string bearer,
        CancellationToken cancellation)
    {
        if (MonitoringRules.Validate(input, clock.GetUtcNow()) is { } error)
            return Error(400, error);

        if (string.IsNullOrEmpty(input.Note)) input = input with { Note = null };
        var existing = await Find(input, cancellation);
        if (existing is not null) return Replay(existing, id, input);

        var item = await catalog.Get(id, bearer, cancellation);
        if (item is null) return Error(404, "Equipamento ativo não encontrado.");
        // Device identity is the stable equipment prefix from Java.
        if (!string.Equals(item.Prefix, input.DeviceId, StringComparison.Ordinal))
            return Error(400, "O deviceId deve coincidir com o prefixo do equipamento.");
        if (input.Volume > item.Capacity)
            return Error(400, "O volume excede a capacidade cadastrada.");

        var state = await db.Cursors.SingleOrDefaultAsync(row => row.SensorId == id, cancellation);
        var timestamp = input.At.UtcTicks;
        if (state is not null && timestamp <= state.LastTicks)
        {
            existing = await Find(input, cancellation);
            return existing is not null ? Replay(existing, id, input) :
                Error(409, "A medição precisa ser posterior à última leitura do equipamento.");
        }

        if (state is null)
        {
            state = new SensorCursor { SensorId = id };
            db.Cursors.Add(state);
        }
        var reading = new Measurement
        {
            SensorId = id, DeviceId = input.DeviceId!, SampleId = input.SampleId!, AtTicks = timestamp,
            Volume = input.Volume, LevelCm = input.LevelCm, Note = input.Note, Source = "http",
            Capacity = item.Capacity, Limit = item.Limit,
            Status = MonitoringRules.ReadingStatus(input.Volume, item.Limit)
        };
        db.Measurements.Add(reading);
        if (reading.Status == MonitoringRules.Alarm)
        {
            state.RecoveryReadings = 0;
            if (state.ActiveAlarmId is null)
            {
                var alarm = new AlarmEvent { SensorId = id, StartTicks = timestamp };
                db.Events.Add(alarm);
                state.ActiveAlarmId = alarm.Id;
            }
        }
        else if (state.ActiveAlarmId is { } active && ++state.RecoveryReadings >= 2)
        {
            var alarm = await db.Events.SingleAsync(row => row.Id == active, cancellation);
            alarm.EndTicks = timestamp;
            state.ActiveAlarmId = null;
            state.RecoveryReadings = 0;
        }
        state.LastTicks = timestamp;
        state.Version++;
        try
        {
            // EF saves the measurement, event and cursor atomically. The cursor version protects concurrent writes.
            await db.SaveChangesAsync(cancellation);
        }
        catch (DbUpdateException ex) when (IsWriteConflict(ex))
        {
            db.ChangeTracker.Clear();
            existing = await Find(input, cancellation);
            return existing is not null ? Replay(existing, id, input) :
                Error(409, "Outra medição foi gravada simultaneamente. Consulte a última leitura e tente novamente.");
        }
        return Results.Json(Receipt(reading), statusCode: 201);
    }

    private Task<Measurement?> Find(ReadingInput input, CancellationToken cancellation) =>
        db.Measurements.AsNoTracking().SingleOrDefaultAsync(row =>
            row.DeviceId == input.DeviceId && row.SampleId == input.SampleId, cancellation);

    private static bool IsWriteConflict(DbUpdateException error) =>
        error is DbUpdateConcurrencyException ||
        error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } ||
        error.InnerException is Oracle.ManagedDataAccess.Client.OracleException { Number: 1 or 8177 };

    private static IResult Replay(Measurement row, string id, ReadingInput input) =>
        row.SensorId == id && row.AtTicks == input.At.UtcTicks && row.Volume == input.Volume &&
        row.LevelCm == input.LevelCm && row.Note == input.Note
            ? Results.Ok(Receipt(row))
            : Error(409, "Esta combinação de deviceId e sampleId já foi usada com outros dados.");

    private static IResult Error(int status, string message) => Results.Json(new ApiError(message), statusCode: status);

    private static ReadingReceipt Receipt(Measurement row) =>
        new(row.SensorId, row.DeviceId, row.SampleId, At(row.AtTicks), row.Status);

    private Sensor ToSensor(CatalogSensor item, SensorCursor? state, Measurement? last, Sample[] samples,
        SensorEvent[] events, int alarms)
    {
        var interval = item.ExpectedIntervalMinutes > 0
            ? item.ExpectedIntervalMinutes : MonitoringRules.DefaultIntervalMinutes;
        var lastAt = last is null ? (DateTimeOffset?)null : At(last.AtTicks);
        var offline = MonitoringRules.IsOffline(lastAt, clock.GetUtcNow(), interval);
        var status = offline ? MonitoringRules.Offline : state?.ActiveAlarmId is not null
            ? MonitoringRules.Alarm : last!.Status;
        var reason = status switch
        {
            MonitoringRules.Offline => last is null ? "Sem leituras recebidas." : "Leitura fora do intervalo esperado.",
            MonitoringRules.Alarm => "Limite atingido ou aguardando confirmação de normalização.",
            MonitoringRules.Warning => "Volume próximo do limite operacional.",
            _ => "Volume abaixo da faixa de alerta."
        };
        return new(item.Id, item.Prefix, item.Name, item.Address, item.Neighborhood, item.Coordinate,
            item.Capacity, item.Limit, item.EquipmentType, item.RadiusMeters, interval, item.PostalAddress,
            status, last?.Volume, lastAt, offline ? null : MonitoringRules.EstimateHours(samples, item.Limit),
            alarms, samples.Select(sample => sample.Volume).ToArray(), samples, events, reason, last?.Source);
    }

    private static Sample ToSample(Measurement row) =>
        new(row.SampleId, At(row.AtTicks), row.Volume, row.LevelCm, row.Status, row.Capacity, row.Limit, row.Source, row.Note);

    private static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);
}
