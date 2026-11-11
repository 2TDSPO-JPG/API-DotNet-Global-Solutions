using Saneia.Domain.Telemetry;

namespace Saneia.Tests;

public sealed class MonitoringRulesTests
{
    [Theory]
    [InlineData(63, "normal")]
    [InlineData(64, "warning")]
    [InlineData(79, "warning")]
    [InlineData(80, "alarm")]
    [InlineData(100, "alarm")]
    public void ClassifiesBothThresholdBoundaries(int volume, string status) =>
        Assert.Equal(status, MonitoringRules.ReadingStatus(volume, 80));

    [Fact]
    public void SensorGoesOfflineAtTheConfiguredInterval()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(MonitoringRules.IsOffline(null, now, 5));
        Assert.False(MonitoringRules.IsOffline(now.AddMinutes(-4), now, 5));
        Assert.True(MonitoringRules.IsOffline(now.AddMinutes(-5), now, 5));
    }

    [Fact]
    public void EstimateRequiresAnIncreasingTrend()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0.5, MonitoringRules.EstimateHours(now.AddMinutes(-30), 60, now, 70, 80));
        Assert.Null(MonitoringRules.EstimateHours(now.AddMinutes(-30), 60, now, 55, 80));
        Assert.Null(MonitoringRules.EstimateHours(now, 60, now, 70, 80));
    }

    [Fact]
    public void AlarmRequiresTwoConsecutiveReadingsBelowLimitToRecover()
    {
        var cursor = new SensorCursor { SensorId = "21" };

        AlarmChange Apply(long tick, string status) => cursor.Apply(new Measurement
        {
            SensorId = "21", DeviceId = "S21", SampleId = tick.ToString(),
            AtTicks = tick, Status = status, Source = "test"
        });

        var opened = Assert.IsType<AlarmEvent>(Apply(1, MonitoringRules.Alarm).Opened);
        Assert.Null(Apply(2, MonitoringRules.Normal).ClosedId);
        Assert.Null(Apply(3, MonitoringRules.Alarm).Opened);
        Assert.Null(Apply(4, MonitoringRules.Warning).ClosedId);
        Assert.Equal(opened.Id, cursor.ActiveAlarmId);
        Assert.Equal(opened.Id, Apply(5, MonitoringRules.Normal).ClosedId);
        Assert.Null(cursor.ActiveAlarmId);
    }
}
