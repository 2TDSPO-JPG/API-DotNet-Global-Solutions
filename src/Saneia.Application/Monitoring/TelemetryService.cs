using Saneia.Domain.Telemetry;

namespace Saneia.Application.Monitoring;

public sealed class TelemetryService(ISensorCatalog catalog, ITelemetryStore store, TimeProvider clock)
{
    public async Task<(Sensor[] Items, int TotalPages)> Map(string bearer, int page, int size,
        CancellationToken cancellation)
    {
        var catalogPage = await catalog.List(bearer, page, size, cancellation);
        var now = clock.GetUtcNow();
        var ids = catalogPage.Items.Select(sensor => sensor.Id).ToArray();
        var snapshots = await store.ReadMap(ids, now.AddMonths(-6).UtcTicks, cancellation);
        var items = catalogPage.Items.Select(item =>
            ToSensor(item, snapshots.GetValueOrDefault(item.Id) ?? SensorSnapshot.Empty, now)).ToArray();
        return (items, catalogPage.TotalPages);
    }

    public async Task<Sensor?> Detail(string id, string bearer, CancellationToken cancellation)
    {
        var item = await catalog.Get(id, bearer, cancellation);
        if (item is null) return null;

        var now = clock.GetUtcNow();
        var snapshot = await store.ReadDetail(id, now.AddMonths(-6).UtcTicks, cancellation);
        return ToSensor(item, snapshot, now);
    }

    public async Task<ReadingOutcome> Ingest(string id, ReadingCommand input, string bearer,
        CancellationToken cancellation)
    {
        if (ReadingValidation.Validate(input, clock.GetUtcNow()) is { } error)
            return new ReadingOutcome.Invalid(error);

        if (string.IsNullOrEmpty(input.Note)) input = input with { Note = null };
        var existing = await store.Find(input.DeviceId!, input.SampleId!, cancellation);
        if (existing is not null) return Replay(existing, id, input);

        var item = await catalog.Get(id, bearer, cancellation);
        if (item is null) return new ReadingOutcome.SensorNotFound();
        if (!string.Equals(item.Prefix, input.DeviceId, StringComparison.Ordinal))
            return new ReadingOutcome.Invalid("O deviceId deve coincidir com o prefixo do equipamento.");
        return await Append(id, item.Capacity, item.Limit, input, "http", cancellation);
    }

    public async Task<ReadingOutcome> IngestMqtt(DeviceCatalog item, string sampleId, decimal volume,
        decimal level, CancellationToken cancellation)
    {
        var input = new ReadingCommand(item.Prefix, sampleId, clock.GetUtcNow(), volume, level, null);
        if (ReadingValidation.Validate(input, input.At) is { } error)
            return new ReadingOutcome.Invalid(error);
        var existing = await store.Find(item.Prefix, sampleId, cancellation);
        if (existing is not null)
            return Replay(existing, item.Id, input, "mqtt");
        return await Append(item.Id, item.Capacity, item.Limit, input, "mqtt", cancellation);
    }

    private async Task<ReadingOutcome> Append(string id, decimal capacity, decimal limit, ReadingCommand input,
        string source, CancellationToken cancellation)
    {
        if (input.Volume > capacity)
            return new ReadingOutcome.Invalid("O volume excede a capacidade cadastrada.");

        var cursor = await store.FindCursor(id, cancellation);
        var timestamp = input.At.UtcTicks;
        if (cursor is not null && timestamp <= cursor.LastTicks)
            return await ReplayOrConflict(id, input, source,
                "A medição precisa ser posterior à última leitura do equipamento.", cancellation);

        var previousVersion = cursor?.Version;
        cursor ??= new SensorCursor { SensorId = id };
        var reading = new Measurement
        {
            SensorId = id, DeviceId = input.DeviceId!, SampleId = input.SampleId!, AtTicks = timestamp,
            Volume = input.Volume, LevelCm = input.LevelCm, Note = input.Note, Source = source,
            Capacity = capacity, Limit = limit,
            Status = MonitoringRules.ReadingStatus(input.Volume, limit)
        };

        var alarm = cursor.Apply(reading);
        if (await store.TryAppend(reading, cursor, previousVersion, alarm, cancellation))
            return new ReadingOutcome.Created(Receipt(reading));

        return await ReplayOrConflict(id, input, source,
            "Outra medição foi gravada simultaneamente. Consulte a última leitura e tente novamente.", cancellation);
    }

    private async Task<ReadingOutcome> ReplayOrConflict(string id, ReadingCommand input, string source, string message,
        CancellationToken cancellation)
    {
        var existing = await store.Find(input.DeviceId!, input.SampleId!, cancellation);
        return existing is not null ? Replay(existing, id, input, source) : new ReadingOutcome.Conflict(message);
    }

    private static ReadingOutcome Replay(Measurement row, string id, ReadingCommand input, string source = "http") =>
        row.SensorId == id && row.Source == source && (source == "mqtt" || row.AtTicks == input.At.UtcTicks) &&
        row.Volume == input.Volume &&
        row.LevelCm == input.LevelCm && row.Note == input.Note
            ? new ReadingOutcome.Replayed(Receipt(row))
            : new ReadingOutcome.Conflict("Esta combinação de deviceId e sampleId já foi usada com outros dados.");

    private static ReadingReceipt Receipt(Measurement row) =>
        new(row.SensorId, row.DeviceId, row.SampleId, At(row.AtTicks), row.Status);

    private static Sensor ToSensor(CatalogSensor item, SensorSnapshot snapshot, DateTimeOffset now)
    {
        var interval = item.ExpectedIntervalMinutes > 0
            ? item.ExpectedIntervalMinutes : MonitoringRules.DefaultIntervalMinutes;
        var last = snapshot.Latest;
        var lastAt = last is null ? (DateTimeOffset?)null : At(last.AtTicks);
        var offline = MonitoringRules.IsOffline(lastAt, now, interval);
        var status = offline ? MonitoringRules.Offline : snapshot.HasActiveAlarm
            ? MonitoringRules.Alarm : last!.Status;
        var reason = status switch
        {
            MonitoringRules.Offline => last is null ? "Sem leituras recebidas." : "Leitura fora do intervalo esperado.",
            MonitoringRules.Alarm => "Limite atingido ou aguardando confirmação de normalização.",
            MonitoringRules.Warning => "Volume próximo do limite operacional.",
            _ => "Volume abaixo da faixa de alerta."
        };
        var samples = snapshot.Readings.Select(ToSample).ToArray();
        var events = snapshot.Events.Select(ToEvent).ToArray();
        var eta = !offline && samples.Length >= 2
            ? MonitoringRules.EstimateHours(samples[^2].At, samples[^2].Volume, samples[^1].At, samples[^1].Volume, item.Limit)
            : null;
        return new(item.Id, item.Prefix, item.Name, item.Address, item.Neighborhood, item.Coordinate,
            item.Capacity, item.Limit, item.EquipmentType, item.RadiusMeters, interval, item.PostalAddress,
            status, last?.Volume, lastAt, eta, snapshot.AlarmCount, samples.Select(sample => sample.Volume).ToArray(),
            samples, events, reason, last?.Source);
    }

    private static Sample ToSample(Measurement row) =>
        new(row.SampleId, At(row.AtTicks), row.Volume, row.LevelCm, row.Status, row.Capacity, row.Limit, row.Source, row.Note);

    private static SensorEvent ToEvent(AlarmEvent row) =>
        new(row.Id.ToString(), MonitoringRules.Alarm, "level_alarm", At(row.StartTicks),
            row.EndTicks is { } end ? At(end) : null, "O volume atingiu o limite operacional.");

    private static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);
}
