using Microsoft.EntityFrameworkCore;

namespace Saneia.Api.Data;

public sealed class TelemetryDb(DbContextOptions<TelemetryDb> options) : DbContext(options)
{
    public DbSet<Measurement> Measurements => Set<Measurement>();
    public DbSet<AlarmEvent> Events => Set<AlarmEvent>();
    public DbSet<SensorCursor> Cursors => Set<SensorCursor>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Measurement>(table =>
        {
            table.ToTable("SANEIA_NET_READINGS");
            table.HasKey(row => row.Id);
            table.HasIndex(row => new { row.DeviceId, row.SampleId }).IsUnique();
            table.HasIndex(row => new { row.SensorId, row.AtTicks });
            table.Property(row => row.DeviceId).HasMaxLength(80);
            table.Property(row => row.SampleId).HasMaxLength(80);
            table.Property(row => row.SensorId).HasMaxLength(80);
            table.Property(row => row.Note).HasMaxLength(300);
            table.Property(row => row.Status).HasMaxLength(12);
            table.Property(row => row.Source).HasMaxLength(20);
            table.Property(row => row.Volume).HasPrecision(28, 18);
            table.Property(row => row.LevelCm).HasPrecision(28, 18);
            table.Property(row => row.Capacity).HasPrecision(28, 18);
            table.Property(row => row.Limit).HasPrecision(28, 18);
        });
        model.Entity<AlarmEvent>(table =>
        {
            table.ToTable("SANEIA_NET_EVENTS");
            table.HasKey(row => row.Id);
            table.HasIndex(row => new { row.SensorId, row.StartTicks });
            table.Property(row => row.SensorId).HasMaxLength(80);
        });
        model.Entity<SensorCursor>(table =>
        {
            table.ToTable("SANEIA_NET_SENSOR_STATE");
            table.HasKey(row => row.SensorId);
            table.Property(row => row.SensorId).HasMaxLength(80);
            table.Property(row => row.Version).IsConcurrencyToken();
        });
    }
}

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

public sealed class SensorCursor
{
    public required string SensorId { get; set; }
    public long LastTicks { get; set; }
    public long Version { get; set; }
    public Guid? ActiveAlarmId { get; set; }
    public int RecoveryReadings { get; set; }
}
