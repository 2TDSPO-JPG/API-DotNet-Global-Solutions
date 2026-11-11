using Saneia.Domain.Telemetry;

namespace Saneia.Application.Monitoring;

public interface ISensorCatalog
{
    Task<CatalogPage> List(string bearer, int page, int size, CancellationToken cancellation);
    Task<CatalogSensor?> Get(string id, string bearer, CancellationToken cancellation);
}

public interface ITelemetryStore
{
    Task<IReadOnlyDictionary<string, SensorSnapshot>> ReadMap(string[] ids, long alarmsFrom, CancellationToken cancellation);
    Task<SensorSnapshot> ReadDetail(string id, long alarmsFrom, CancellationToken cancellation);
    Task<TelemetrySummary> Summarize(string id, DateTimeOffset from, DateTimeOffset to,
        DateTimeOffset now, int intervalMinutes, decimal limit, CancellationToken cancellation);
    Task<Measurement?> Find(string deviceId, string sampleId, CancellationToken cancellation);
    Task<SensorCursor?> FindCursor(string sensorId, CancellationToken cancellation);
    Task<bool> TryAppend(Measurement reading, SensorCursor cursor, long? previousVersion, AlarmChange alarm,
        CancellationToken cancellation);
}

public sealed record CatalogPage(CatalogSensor[] Items, int TotalPages);
public sealed class CatalogUnavailable(string message, Exception? inner = null) : Exception(message, inner);

public sealed record SensorSnapshot(bool HasActiveAlarm, Measurement? Latest, Measurement[] Readings,
    AlarmEvent[] Events, int AlarmCount)
{
    public static SensorSnapshot Empty { get; } = new(false, null, [], [], 0);
}
