using Ghostrig.Kernel.Cost;
using Ghostrig.Kernel.Determinism;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Events;
using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Memory;
using Ghostrig.Kernel.Network;
using Ghostrig.Kernel.Telemetry;
using Ghostrig.Kernel.Thermal;
using Ghostrig.Kernel.Time;
using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel;

/// <summary>
/// The GHOSTRIG kernel: sole authority on simulated hardware state, cost billing, thermals and the simulated
/// internet (Doc 2 §1). Single-threaded and fully deterministic; construct it with <see cref="KernelBuilder"/>.
/// </summary>
public sealed class GhostrigKernel
{
    private RigSpecification? _installedRig;
    private TelemetrySnapshot _acousticBaseline = TelemetrySnapshot.Empty;
    private SimTime _acousticBaselineAt;

    internal GhostrigKernel(
        ulong seed,
        SimClock clock,
        DeterministicRandom random,
        HardwareCatalog catalog,
        HardwareBay bay,
        IBillingProfile billing,
        WorldCalendar calendar,
        EventBus events,
        NetworkSimulator network,
        NetworkNode localNode,
        Principal localUser,
        long thermalEnergyNanojoules = 0)
    {
        Seed = seed;
        Clock = clock;
        Random = random;
        Catalog = catalog;
        Bay = bay;
        Billing = billing;
        Calendar = calendar;
        Events = events;
        Network = network;
        LocalNode = localNode;
        LocalUser = localUser;
        Telemetry = new TelemetryRecorder();
        Memory = new MemoryManager();
        Thermal = new ThermalModel(bay.Chassis, thermalEnergyNanojoules);
    }

    public ulong Seed { get; }
    public SimClock Clock { get; }
    public DeterministicRandom Random { get; }
    public HardwareCatalog Catalog { get; }
    public HardwareBay Bay { get; }
    public IBillingProfile Billing { get; }
    public WorldCalendar Calendar { get; }
    public EventBus Events { get; }
    public NetworkSimulator Network { get; }
    public TelemetryRecorder Telemetry { get; }
    public MemoryManager Memory { get; }
    public ThermalModel Thermal { get; }

    /// <summary>Kael's own rig on the simulated network.</summary>
    public NetworkNode LocalNode { get; }

    /// <summary>The identity local applications run as (Kael is root on his own machine).</summary>
    public Principal LocalUser { get; }

    public SimTime Now => Clock.Now;

    public PowerState PowerState => Bay.PowerState;

    public int TemperatureMilliCelsius => Thermal.TemperatureMilliCelsius;

    /// <summary>Specification of the hardware installed at the last boot.</summary>
    public RigSpecification InstalledRig => _installedRig ?? throw new HardwareStateException("The rig is not powered on.");

    #region Power and hardware

    /// <summary>Boots the bay: validates hardware, resets volatile RAM and consumes the chassis boot time.</summary>
    public void PowerOn()
    {
        var rig = Bay.PowerOn();
        _installedRig = rig;
        Memory.Reset(rig.MemoryCapacityBytes);
        Events.Publish(new SysPowerEvent(PowerState.On));
        Idle(SimDuration.FromMilliseconds(rig.Chassis.BootMilliseconds));
        ResetAcousticWindow();
    }

    /// <summary>Powers the bay down (also the only way out of a kernel panic).</summary>
    public void PowerOff()
    {
        if (Bay.PowerState == PowerState.Off)
        {
            throw new HardwareStateException("The rig is already powered off.");
        }

        Bay.PowerOff();
        _installedRig = null;
        Memory.Reset(0);
        Events.Publish(new SysPowerEvent(PowerState.Off));
    }

    public BaySlot Install(string componentId)
    {
        var slot = Bay.Install(componentId);
        Events.Publish(new SysHardwareChangedEvent(slot, componentId, true));
        return slot;
    }

    public HardwareComponent Remove(BaySlot slot)
    {
        var component = Bay.Remove(slot);
        Events.Publish(new SysHardwareChangedEvent(slot, component.Id, false));
        return component;
    }

    #endregion

    #region Billing

    /// <summary>
    /// Bills one hardware submission: computes its deterministic cost, advances the clock, accumulates heat and
    /// records telemetry. Throws <see cref="KernelPanicException"/> if the chassis crosses its cascade threshold.
    /// </summary>
    public CostReceipt Bill(Submission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var installed = RequireRunning();
        var rig = Billing.ResolveBillingRig(installed);
        var cost = CostModel.Compute(submission, rig);

        var start = Clock.Now;
        var energy = EnergyFor(cost, rig);
        var step = Thermal.Advance(cost.Elapsed, energy, Billing.ThermalCascadeEnabled);
        if (step.CascadeTripped)
        {
            Clock.Advance(step.Consumed);
            Panic(step.TemperatureMilliCelsius);
        }

        Clock.Advance(cost.Elapsed);
        Telemetry.Record(submission, cost);
        return new CostReceipt(submission, cost, start, Clock.Now);
    }

    public IReadOnlyList<CostReceipt> Bill(IEnumerable<Submission> submissions)
    {
        ArgumentNullException.ThrowIfNull(submissions);
        return submissions.Select(Bill).ToList();
    }

    /// <summary>Lets simulated time pass with no billed work (idle power while running, cooling always).</summary>
    public void Idle(SimDuration duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Nanoseconds);
        long energy = 0;
        if (Bay.PowerState == PowerState.On && _installedRig is { } installed)
        {
            var cpu = Billing.ResolveBillingRig(installed).Cpu;
            energy = checked(duration.Nanoseconds * cpu.TdpWatts * cpu.IdlePermille / 1000);
        }

        var step = Thermal.Advance(duration, energy, Billing.ThermalCascadeEnabled && Bay.PowerState == PowerState.On);
        if (step.CascadeTripped)
        {
            Clock.Advance(step.Consumed);
            Panic(step.TemperatureMilliCelsius);
        }

        Clock.Advance(duration);
    }

    #endregion

    #region Telemetry

    /// <summary>Acoustic intensities over the window since the previous sample (Doc 3 §3.3).</summary>
    public AcousticSignals SampleAcoustics()
    {
        if (Bay.PowerState != PowerState.On || _installedRig is null)
        {
            return AcousticSignals.Silent;
        }

        var now = Telemetry.Snapshot();
        var signals = AcousticModel.Compute(now.Since(_acousticBaseline), Clock.Now - _acousticBaselineAt,
            Thermal.TemperatureMilliCelsius, _installedRig);
        _acousticBaseline = now;
        _acousticBaselineAt = Clock.Now;
        return signals;
    }

    private void ResetAcousticWindow()
    {
        _acousticBaseline = Telemetry.Snapshot();
        _acousticBaselineAt = Clock.Now;
    }

    #endregion

    #region Internals

    private RigSpecification RequireRunning() => Bay.PowerState switch
    {
        PowerState.On => _installedRig!,
        PowerState.Panicked => throw new KernelPanicException("The kernel has halted after a hardware protection fault; power-cycle the rig."),
        _ => throw new HardwareStateException("The rig is powered off."),
    };

    /// <summary>Heat injected by a submission: CPU busy power, idle power for the rest, plus interrupt heating.</summary>
    private static long EnergyFor(CostBreakdown cost, RigSpecification rig)
    {
        var cpu = rig.Cpu;
        var busy = Math.Min(cost.CpuBusyTime.Nanoseconds, cost.Elapsed.Nanoseconds);
        var idle = cost.Elapsed.Nanoseconds - busy;
        // W · ns = nJ
        return checked((busy * cpu.TdpWatts) + (idle * cpu.TdpWatts * cpu.IdlePermille / 1000)
                       + (cost.Interrupts * rig.Bus.InterruptEnergyNanojoules));
    }

    private void Panic(int temperatureMilliCelsius)
    {
        Bay.Panic();
        Events.Publish(new SysThermalCascadeEvent(temperatureMilliCelsius));
        throw new KernelPanicException(
            $"KERNEL PANIC: thermal protection fault at {temperatureMilliCelsius / 1000.0:0.0}°C — execution halted.");
    }

    internal void RestoreRuntime(RigSpecification? installedRig) => _installedRig = installedRig;

    #endregion
}
