using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Time;

namespace Ghostrig.Kernel.Thermal;

/// <summary>Outcome of advancing the thermal model across an interval.</summary>
/// <param name="TemperatureMilliCelsius">Chassis temperature at the end of the step.</param>
/// <param name="PeakMilliCelsius">Highest temperature reached during the step.</param>
/// <param name="CascadeTripped">Whether the hardware protection threshold was crossed.</param>
/// <param name="Consumed">Simulated time actually integrated (shorter than requested when a cascade trips).</param>
public readonly record struct ThermalStep(int TemperatureMilliCelsius, int PeakMilliCelsius, bool CascadeTripped, SimDuration Consumed);

/// <summary>
/// Lumped-capacitance chassis thermal model with Newtonian cooling, integrated in fixed-point integer math so it
/// is deterministic across platforms. State is the heat energy stored above ambient, in nanojoules.
/// </summary>
public sealed class ThermalModel
{
    /// <summary>Integration sub-steps per thermal time constant; keeps explicit Euler stable and accurate.</summary>
    private const long StepsPerTimeConstant = 20;

    private readonly ChassisSpec _chassis;
    private readonly long _maxStepNs;

    public ThermalModel(ChassisSpec chassis, long storedEnergyNanojoules = 0)
    {
        ArgumentNullException.ThrowIfNull(chassis);
        ArgumentOutOfRangeException.ThrowIfNegative(storedEnergyNanojoules);
        _chassis = chassis;
        StoredEnergyNanojoules = storedEnergyNanojoules;

        // τ = C / k  →  ns = (J/°C · 1e9) / (mW/°C / 1000) = C · 1e12 / k_mW
        var tauNs = (Int128)chassis.HeatCapacityJoulesPerCelsius * 1_000_000_000_000L / chassis.CoolingMilliwattsPerCelsius;
        _maxStepNs = (long)Int128.Max(1, tauNs / StepsPerTimeConstant);
    }

    public long StoredEnergyNanojoules { get; private set; }

    public int TemperatureMilliCelsius => TemperatureFor(StoredEnergyNanojoules);

    /// <summary>
    /// Advances the model by <paramref name="interval"/>, injecting <paramref name="inputNanojoules"/> spread
    /// uniformly across it. Stops early (state frozen at the trip point) if the cascade threshold is crossed
    /// and <paramref name="cascadeEnabled"/> is set.
    /// </summary>
    public ThermalStep Advance(SimDuration interval, long inputNanojoules, bool cascadeEnabled)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(interval.Nanoseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(inputNanojoules);

        var peak = TemperatureMilliCelsius;
        if (interval.Nanoseconds == 0)
        {
            StoredEnergyNanojoules = checked(StoredEnergyNanojoules + inputNanojoules);
            return Finish(ref peak, cascadeEnabled, SimDuration.Zero);
        }

        var remainingNs = interval.Nanoseconds;
        var remainingInput = inputNanojoules;
        var capacityScale = (Int128)_chassis.HeatCapacityJoulesPerCelsius * 1_000_000_000_000L;

        while (remainingNs > 0)
        {
            var dt = Math.Min(remainingNs, _maxStepNs);
            var input = (long)((Int128)remainingInput * dt / remainingNs);
            remainingInput -= input;
            remainingNs -= dt;

            var energy = (Int128)StoredEnergyNanojoules + input;
            var cooled = energy * _chassis.CoolingMilliwattsPerCelsius * dt / capacityScale;
            StoredEnergyNanojoules = (long)Int128.Max(0, energy - cooled);

            var step = Finish(ref peak, cascadeEnabled, new SimDuration(interval.Nanoseconds - remainingNs));
            if (step.CascadeTripped)
            {
                return step;
            }
        }

        return new ThermalStep(TemperatureMilliCelsius, peak, false, interval);
    }

    internal void Restore(long storedEnergyNanojoules) => StoredEnergyNanojoules = storedEnergyNanojoules;

    private ThermalStep Finish(ref int peak, bool cascadeEnabled, SimDuration consumed)
    {
        var t = TemperatureMilliCelsius;
        peak = Math.Max(peak, t);
        return new ThermalStep(t, peak, cascadeEnabled && t >= _chassis.CascadeMilliCelsius, consumed);
    }

    private int TemperatureFor(long energyNanojoules) =>
        // ΔT(m°C) = E(nJ) / (C(J/°C) · 1e6)
        (int)Math.Min(int.MaxValue, _chassis.AmbientMilliCelsius + (energyNanojoules / ((long)_chassis.HeatCapacityJoulesPerCelsius * 1_000_000)));
}
