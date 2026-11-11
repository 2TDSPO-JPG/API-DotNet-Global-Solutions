using System.Text.Json.Serialization;
using Saneia.Application.Monitoring;

namespace Saneia.Api.Monitoring;

public sealed record ReadingInput(string? DeviceId, string? SampleId, DateTimeOffset At,
    [property: JsonRequired] decimal Volume, decimal? LevelCm = null, string? Note = null)
{
    public ReadingCommand ToCommand() => new(DeviceId, SampleId, At, Volume, LevelCm, Note);
}

public sealed record ApiError(string Message);
