namespace Saneia.Domain.Telemetry;

public static class MonitoringRules
{
    public const string Normal = "normal";
    public const string Warning = "warning";
    public const string Alarm = "alarm";
    public const string Offline = "offline";
    public const int DefaultIntervalMinutes = 5;

    public static string ReadingStatus(decimal volume, decimal limit) =>
        volume >= limit ? Alarm : volume >= limit * 0.8m ? Warning : Normal;

    public static bool IsOffline(DateTimeOffset? lastReading, DateTimeOffset now, int intervalMinutes) =>
        lastReading is null || now - lastReading.Value >= TimeSpan.FromMinutes(intervalMinutes);

    public static double? EstimateHours(DateTimeOffset previousAt, decimal previousVolume,
        DateTimeOffset currentAt, decimal currentVolume, decimal limit)
    {
        var hours = (currentAt - previousAt).TotalHours;
        if (currentVolume >= limit) return 0;
        if (hours <= 0 || currentVolume <= previousVolume) return null;
        return Math.Round(hours * (double)((limit - currentVolume) / (currentVolume - previousVolume)), 2);
    }
}
