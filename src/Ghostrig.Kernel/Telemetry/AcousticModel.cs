using Ghostrig.Kernel.Cost;
using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Time;

namespace Ghostrig.Kernel.Telemetry;

/// <summary>
/// Intensities (0–1000) of each physical sound the rig makes. The host audio engine synthesizes these; the
/// mapping is the single authoritative table in Doc 3 §3.3.
/// </summary>
public readonly record struct AcousticSignals(int Fan, int CoilWhine, int PlatterChatter, int AudioArtifacts)
{
    public static readonly AcousticSignals Silent = default;
}

/// <summary>Derives <see cref="AcousticSignals"/> from a telemetry window and the current temperature.</summary>
public static class AcousticModel
{
    /// <summary>Raw (uncoalesced) interrupts per simulated millisecond that produce maximum coil whine.</summary>
    public const long CoilWhineSaturationPerMs = 200;

    /// <summary>Storage commands per simulated millisecond that produce maximum chatter on a fully noisy platter.</summary>
    public const long ChatterSaturationPerMs = 50;

    /// <summary>Audio cache misses (buffer flushes) per simulated millisecond that produce maximum artifacts.</summary>
    public const long ArtifactSaturationPerMs = 2;

    public static AcousticSignals Compute(TelemetrySnapshot window, SimDuration windowLength, int temperatureMilliCelsius,
        RigSpecification rig)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rig);

        var fanSpan = rig.Chassis.CascadeMilliCelsius - rig.Chassis.AmbientMilliCelsius;
        var fan = Clamp((long)(temperatureMilliCelsius - rig.Chassis.AmbientMilliCelsius) * 1000 / fanSpan);

        if (windowLength.Nanoseconds <= 0)
        {
            return new AcousticSignals(fan, 0, 0, 0);
        }

        var windowMs = Math.Max(1, windowLength.Nanoseconds / SimDuration.NanosPerMillisecond);

        long rawInterrupts = 0;
        foreach (var stats in window.Devices.Values)
        {
            rawInterrupts += stats.RawInterrupts;
        }

        var coil = Clamp(rawInterrupts * 1000 / (CoilWhineSaturationPerMs * windowMs));

        var storage = window[DeviceClass.Storage];
        var noise = rig.Storage.Where(s => s.Medium == StorageMedium.Platter).Select(s => s.PlatterNoise).DefaultIfEmpty(0).Max();
        var chatter = Clamp(storage.Submissions * noise / (ChatterSaturationPerMs * windowMs));

        var artifacts = Clamp(window[DeviceClass.Audio].CacheMisses * 1000 / (ArtifactSaturationPerMs * windowMs));

        return new AcousticSignals(fan, coil, chatter, artifacts);
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, 1000);
}
