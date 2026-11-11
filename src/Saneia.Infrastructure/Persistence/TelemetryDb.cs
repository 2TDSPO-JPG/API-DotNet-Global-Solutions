using Microsoft.EntityFrameworkCore;

using Saneia.Domain.Telemetry;

namespace Saneia.Infrastructure.Persistence;

internal sealed class TelemetryDb(DbContextOptions<TelemetryDb> options) : DbContext(options)
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
