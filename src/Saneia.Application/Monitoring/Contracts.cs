using System.Text.Json;

namespace Saneia.Application.Monitoring;

public sealed record Coordinate(double Latitude, double Longitude);
public sealed record DeviceCatalog(string Id, string Prefix, decimal Capacity, decimal Limit);

public sealed record CatalogSensor(
    string Id, string Prefix, string Name, string Address, string Neighborhood,
    Coordinate Coordinate, decimal Capacity, decimal Limit, string? EquipmentType,
    double RadiusMeters, int ExpectedIntervalMinutes, JsonElement? PostalAddress);

public sealed record Sensor(
    string Id, string Prefix, string Name, string Address, string Neighborhood, Coordinate Coordinate,
    decimal Capacity, decimal Limit, string? EquipmentType, double RadiusMeters,
    int ExpectedIntervalMinutes, JsonElement? PostalAddress, string Status, decimal? Volume,
    DateTimeOffset? LastReadingAt, double? EtaHours, int AlarmsSixMonths,
    decimal[] Readings, Sample[] Samples, SensorEvent[] Events, string Reason, string? Source);

public sealed record Sample(
    string Id, DateTimeOffset At, decimal Volume, decimal? LevelCm, string Status,
    decimal Capacity, decimal Limit, string Source, string? Note);

public sealed record SensorEvent(string Id, string Status, string Kind, DateTimeOffset At, DateTimeOffset? EndedAt, string Message);

public sealed record ReadingReceipt(string SensorId, string DeviceId, string SampleId, DateTimeOffset At, string Status);

public sealed record TelemetrySummary(int Readings, double? FirstVolume, double? LastVolume,
    double? MinVolume, double? MaxVolume, DateTimeOffset? MaxVolumeAt, double? AverageVolume,
    double? ChangeLiters, int ReadingsAtOrAboveLimit, int AlarmEvents, double? LongestAlarmHours,
    double? EtaHours, string CurrentStatus, DateTimeOffset? LastReadingAt);
