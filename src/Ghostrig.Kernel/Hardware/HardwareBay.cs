using System.Collections.Immutable;
using Ghostrig.Kernel.Errors;

namespace Ghostrig.Kernel.Hardware;

public enum PowerState
{
    Off,
    On,

    /// <summary>Halted by a hardware protection fault; must be powered off before anything else.</summary>
    Panicked,
}

/// <summary>Identifies one physical slot in the bay.</summary>
public readonly record struct BaySlot(ComponentCategory Category, int Index)
{
    public override string ToString() => $"{Category.ToString().ToLowerInvariant()}[{Index}]";
}

/// <summary>
/// The simulated hardware bay in My Rig. Components may only be installed or removed while powered off
/// (Doc 3 §4: power-down hot-swapping).
/// </summary>
public sealed class HardwareBay
{
    private readonly HardwareCatalog _catalog;
    private readonly SortedDictionary<BaySlot, HardwareComponent> _installed = new(BaySlotComparer.Instance);

    public HardwareBay(HardwareCatalog catalog, string chassisId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        Chassis = catalog.Get<ChassisSpec>(chassisId);
    }

    public ChassisSpec Chassis { get; }

    public PowerState PowerState { get; private set; } = PowerState.Off;

    public IReadOnlyDictionary<BaySlot, HardwareComponent> Installed => _installed;

    public int SlotCount(ComponentCategory category) => category switch
    {
        ComponentCategory.Cpu or ComponentCategory.Bus or ComponentCategory.Gpu or ComponentCategory.Audio => 1,
        ComponentCategory.Memory => Chassis.MemorySlots,
        ComponentCategory.Storage => Chassis.StorageSlots,
        ComponentCategory.Network => Chassis.NetworkSlots,
        ComponentCategory.Chassis => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>Installs a catalog component into the first free slot of its category.</summary>
    public BaySlot Install(string componentId)
    {
        RequirePoweredOff("install hardware");
        var component = _catalog.Get(componentId);
        if (component.Category == ComponentCategory.Chassis)
        {
            throw new HardwareConfigurationException("The chassis is the bay itself and cannot be slotted.");
        }

        var capacity = SlotCount(component.Category);
        for (var i = 0; i < capacity; i++)
        {
            var slot = new BaySlot(component.Category, i);
            if (_installed.TryAdd(slot, component))
            {
                return slot;
            }
        }

        throw new HardwareConfigurationException(
            $"No free {component.Category} slot for '{component.Name}' (chassis provides {capacity}).");
    }

    /// <summary>Installs into an explicit slot (used when restoring saves).</summary>
    public void InstallAt(BaySlot slot, string componentId)
    {
        RequirePoweredOff("install hardware");
        var component = _catalog.Get(componentId);
        if (component.Category != slot.Category || slot.Index < 0 || slot.Index >= SlotCount(slot.Category))
        {
            throw new HardwareConfigurationException($"'{component.Name}' cannot be installed in slot {slot}.");
        }

        if (!_installed.TryAdd(slot, component))
        {
            throw new HardwareConfigurationException($"Slot {slot} is already occupied.");
        }
    }

    public HardwareComponent Remove(BaySlot slot)
    {
        RequirePoweredOff("remove hardware");
        if (!_installed.Remove(slot, out var component))
        {
            throw new HardwareConfigurationException($"Slot {slot} is empty.");
        }

        return component;
    }

    /// <summary>Validates the configuration and returns the effective specification for a boot.</summary>
    internal RigSpecification PowerOn()
    {
        if (PowerState != PowerState.Off)
        {
            throw new HardwareStateException($"Cannot power on from state {PowerState}.");
        }

        var rig = RigSpecification.From(Chassis, _installed.Values, _catalog.Constants);
        PowerState = PowerState.On;
        return rig;
    }

    internal void PowerOff() => PowerState = PowerState.Off;

    internal void Panic() => PowerState = PowerState.Panicked;

    internal void RestorePowerState(PowerState state) => PowerState = state;

    /// <summary>Specification derived from the currently installed parts, without changing power state.</summary>
    public RigSpecification Describe() => RigSpecification.From(Chassis, _installed.Values, _catalog.Constants);

    private void RequirePoweredOff(string action)
    {
        if (PowerState != PowerState.Off)
        {
            throw new HardwareStateException($"Power down the bay before you {action} (current state: {PowerState}).");
        }
    }

    private sealed class BaySlotComparer : IComparer<BaySlot>
    {
        public static readonly BaySlotComparer Instance = new();

        public int Compare(BaySlot x, BaySlot y)
        {
            var c = x.Category.CompareTo(y.Category);
            return c != 0 ? c : x.Index.CompareTo(y.Index);
        }
    }
}

/// <summary>
/// The effective, immutable hardware a cost computation is billed against. Built either from the installed
/// bay (Bare-Metal, Silicon &amp; Bus) or from a reference profile (Street Ghost).
/// </summary>
public sealed record RigSpecification
{
    private RigSpecification(
        ChassisSpec chassis, CpuSpec cpu, ImmutableArray<MemorySpec> memory, ImmutableArray<StorageSpec> storage,
        ImmutableArray<NicSpec> network, BusSpec bus, GpuSpec? gpu, AudioSpec? audio, CostConstants constants)
    {
        Chassis = chassis;
        Cpu = cpu;
        Memory = memory;
        Storage = storage;
        Network = network;
        Bus = bus;
        Gpu = gpu;
        Audio = audio;
        Constants = constants;
    }

    public ChassisSpec Chassis { get; }
    public CpuSpec Cpu { get; }
    public ImmutableArray<MemorySpec> Memory { get; }
    public ImmutableArray<StorageSpec> Storage { get; }
    public ImmutableArray<NicSpec> Network { get; }
    public BusSpec Bus { get; }
    public GpuSpec? Gpu { get; }
    public AudioSpec? Audio { get; }
    public CostConstants Constants { get; }

    public long MemoryCapacityBytes => Memory.Sum(m => (long)m.CapacityMb) * 1024L * 1024L;

    /// <summary>Worst (highest) CAS latency across installed modules: mixed kits run at the slowest timing.</summary>
    public int EffectiveCasLatency => Memory.Max(m => m.CasLatency);

    public StorageSpec PrimaryStorage => Storage[0];

    public NicSpec PrimaryNic => Network.IsEmpty
        ? throw new HardwareConfigurationException("No network interface card is installed.")
        : Network[0];

    public GpuSpec RequireGpu() => Gpu ?? throw new HardwareConfigurationException("No GPU is installed.");

    public AudioSpec RequireAudio() => Audio ?? throw new HardwareConfigurationException("No audio DSP is installed.");

    public static RigSpecification From(ChassisSpec chassis, IEnumerable<HardwareComponent> components, CostConstants constants)
    {
        ArgumentNullException.ThrowIfNull(chassis);
        var parts = components.ToList();
        var cpu = parts.OfType<CpuSpec>().SingleOrDefault()
                  ?? throw new HardwareConfigurationException("Boot failed: no CPU installed.");
        var memory = parts.OfType<MemorySpec>().ToImmutableArray();
        if (memory.IsEmpty)
        {
            throw new HardwareConfigurationException("Boot failed: no memory module installed.");
        }

        var storage = parts.OfType<StorageSpec>().ToImmutableArray();
        if (storage.IsEmpty)
        {
            throw new HardwareConfigurationException("Boot failed: no storage device installed.");
        }

        var bus = parts.OfType<BusSpec>().SingleOrDefault()
                  ?? throw new HardwareConfigurationException("Boot failed: no bus / I/O controller installed.");

        return new RigSpecification(
            chassis, cpu, memory, storage, parts.OfType<NicSpec>().ToImmutableArray(), bus,
            parts.OfType<GpuSpec>().SingleOrDefault(), parts.OfType<AudioSpec>().SingleOrDefault(), constants);
    }
}
