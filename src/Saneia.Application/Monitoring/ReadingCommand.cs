namespace Saneia.Application.Monitoring;

public sealed record ReadingCommand(string? DeviceId, string? SampleId, DateTimeOffset At,
    decimal Volume, decimal? LevelCm, string? Note);

public abstract record ReadingOutcome
{
    public sealed record Created(ReadingReceipt Receipt) : ReadingOutcome;
    public sealed record Replayed(ReadingReceipt Receipt) : ReadingOutcome;
    public sealed record Invalid(string Message) : ReadingOutcome;
    public sealed record SensorNotFound : ReadingOutcome;
    public sealed record Conflict(string Message) : ReadingOutcome;
}
