namespace Saneia.Domain.Telemetry;

public sealed class Measurement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string SensorId { get; set; }
    public required string DeviceId { get; set; }
    public required string SampleId { get; set; }
    public long AtTicks { get; set; }
    public decimal Volume { get; set; }
    public decimal? LevelCm { get; set; }
    public decimal Capacity { get; set; }
    public decimal Limit { get; set; }
    public required string Status { get; set; }
    public required string Source { get; set; }
    public string? Note { get; set; }
}

public sealed class AlarmEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string SensorId { get; set; }
    public long StartTicks { get; set; }
    public long? EndTicks { get; set; }
}
