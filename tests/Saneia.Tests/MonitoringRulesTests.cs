using Saneia.Api.Monitoring;

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
        Sample SampleAt(DateTimeOffset at, decimal volume) =>
            new(Guid.NewGuid().ToString(), at, volume, null, "normal", 100, 80, "test", null);
        var older = SampleAt(now.AddMinutes(-30), 60);
        Assert.Equal(0.5, MonitoringRules.EstimateHours([older, SampleAt(now, 70)], 80));
        Assert.Null(MonitoringRules.EstimateHours([older, SampleAt(now, 55)], 80));
        Assert.Null(MonitoringRules.EstimateHours([older], 80));
    }
}
