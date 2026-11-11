namespace Saneia.Domain.Telemetry;

public sealed class SensorCursor
{
    public required string SensorId { get; init; }
    public long LastTicks { get; private set; }
    public long Version { get; private set; }
    public Guid? ActiveAlarmId { get; private set; }
    public int RecoveryReadings { get; private set; }

    public AlarmChange Apply(Measurement reading)
    {
        if (reading.SensorId != SensorId || reading.AtTicks <= LastTicks)
            throw new ArgumentException("A leitura precisa pertencer ao equipamento e avançar o histórico.", nameof(reading));

        LastTicks = reading.AtTicks;
        Version++;

        if (reading.Status == MonitoringRules.Alarm)
        {
            RecoveryReadings = 0;
            if (ActiveAlarmId is null)
            {
                var alarm = new AlarmEvent { SensorId = SensorId, StartTicks = reading.AtTicks };
                ActiveAlarmId = alarm.Id;
                return new(Opened: alarm);
            }
        }
        else if (ActiveAlarmId is { } active && ++RecoveryReadings >= 2)
        {
            ActiveAlarmId = null;
            RecoveryReadings = 0;
            return new(ClosedId: active);
        }

        return new();
    }
}

public readonly record struct AlarmChange(AlarmEvent? Opened = null, Guid? ClosedId = null);
