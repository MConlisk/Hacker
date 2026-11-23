using System.Collections.Immutable;
using Ghostrig.Kernel.Cost;

namespace Ghostrig.Kernel.Telemetry;

/// <summary>Immutable point-in-time copy of all per-device statistics.</summary>
public sealed record TelemetrySnapshot(ImmutableSortedDictionary<DeviceClass, DriverStats> Devices)
{
    public static readonly TelemetrySnapshot Empty = new(ImmutableSortedDictionary<DeviceClass, DriverStats>.Empty);

    public DriverStats this[DeviceClass device] => Devices.TryGetValue(device, out var s) ? s : default;

    /// <summary>Per-device difference between two snapshots (a telemetry window).</summary>
    public TelemetrySnapshot Since(TelemetrySnapshot earlier)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<DeviceClass, DriverStats>();
        foreach (var (device, stats) in Devices)
        {
            builder[device] = stats - earlier[device];
        }

        return new TelemetrySnapshot(builder.ToImmutable());
    }
}

/// <summary>Aggregates <see cref="DriverStats"/> per device class as the kernel bills submissions.</summary>
public sealed class TelemetryRecorder
{
    private ImmutableSortedDictionary<DeviceClass, DriverStats> _devices = ImmutableSortedDictionary<DeviceClass, DriverStats>.Empty;

    public TelemetrySnapshot Snapshot() => new(_devices);

    public void Record(Submission submission, CostBreakdown cost)
    {
        var current = _devices.TryGetValue(submission.Device, out var s) ? s : default;
        _devices = _devices.SetItem(submission.Device, current.Add(submission, cost));
    }

    internal void Restore(TelemetrySnapshot snapshot) => _devices = snapshot.Devices;
}
