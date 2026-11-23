using System.Collections.Immutable;

namespace Ghostrig.Kernel.Hardware;

/// <summary>Physical slot category in My Rig (Doc 3 §4, Doc 5 §1).</summary>
public enum ComponentCategory
{
    Chassis,
    Cpu,
    Memory,
    Storage,
    Network,
    Bus,
    Gpu,
    Audio,
}

/// <summary>Kind of storage medium; platters seek and chatter, NVMe does not.</summary>
public enum StorageMedium
{
    Platter,
    Solid,
    Nvme,
}

/// <summary>
/// Immutable catalog entry describing a purchasable/installable component. Instances are defined once in
/// <c>content/game/hardware.json</c> (single source of truth, Doc 2 §4.1).
/// </summary>
public abstract record HardwareComponent
{
    protected HardwareComponent(string id, string name, int priceCredits, ImmutableArray<string> vendors, string? defaultDriver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(priceCredits);
        Id = id;
        Name = name;
        PriceCredits = priceCredits;
        Vendors = vendors.IsDefault ? [] : vendors;
        DefaultDriver = defaultDriver;
    }

    public string Id { get; }
    public string Name { get; }
    public int PriceCredits { get; }
    public ImmutableArray<string> Vendors { get; }

    /// <summary>Factory CScript driver bound to the slot after a reboot (e.g. <c>/sys/drivers/net/NetCardDriver.drv</c>).</summary>
    public string? DefaultDriver { get; }

    public abstract ComponentCategory Category { get; }
}

/// <summary>The chassis defines slot counts and the thermal envelope of the whole rig.</summary>
public sealed record ChassisSpec : HardwareComponent
{
    public ChassisSpec(
        string id, string name, int priceCredits, ImmutableArray<string> vendors,
        int memorySlots, int storageSlots, int networkSlots,
        int coolingMilliwattsPerCelsius, int heatCapacityJoulesPerCelsius,
        int ambientMilliCelsius, int cascadeMilliCelsius, int bootMilliseconds)
        : base(id, name, priceCredits, vendors, null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memorySlots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(storageSlots, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(networkSlots);
        ArgumentOutOfRangeException.ThrowIfLessThan(coolingMilliwattsPerCelsius, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(heatCapacityJoulesPerCelsius, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cascadeMilliCelsius, ambientMilliCelsius);
        ArgumentOutOfRangeException.ThrowIfNegative(bootMilliseconds);
        MemorySlots = memorySlots;
        StorageSlots = storageSlots;
        NetworkSlots = networkSlots;
        CoolingMilliwattsPerCelsius = coolingMilliwattsPerCelsius;
        HeatCapacityJoulesPerCelsius = heatCapacityJoulesPerCelsius;
        AmbientMilliCelsius = ambientMilliCelsius;
        CascadeMilliCelsius = cascadeMilliCelsius;
        BootMilliseconds = bootMilliseconds;
    }

    public int MemorySlots { get; }
    public int StorageSlots { get; }
    public int NetworkSlots { get; }

    /// <summary>Heat removed per degree above ambient (Newtonian cooling), in mW/°C.</summary>
    public int CoolingMilliwattsPerCelsius { get; }
    public int HeatCapacityJoulesPerCelsius { get; }
    public int AmbientMilliCelsius { get; }

    /// <summary>Temperature at which the hardware protection fault (Kernel Panic) trips.</summary>
    public int CascadeMilliCelsius { get; }
    public int BootMilliseconds { get; }

    public override ComponentCategory Category => ComponentCategory.Chassis;
}

public sealed record CpuSpec : HardwareComponent
{
    public CpuSpec(string id, string name, int priceCredits, ImmutableArray<string> vendors,
        int clockMhz, int cores, int ipcMilli, int tdpWatts, int idlePermille)
        : base(id, name, priceCredits, vendors, null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clockMhz, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cores, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ipcMilli, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tdpWatts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(idlePermille);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(idlePermille, 1000);
        ClockMhz = clockMhz;
        Cores = cores;
        IpcMilli = ipcMilli;
        TdpWatts = tdpWatts;
        IdlePermille = idlePermille;
    }

    public int ClockMhz { get; }
    public int Cores { get; }

    /// <summary>Instructions per cycle × 1000 (fixed point); consumed by the CScript VM step metering.</summary>
    public int IpcMilli { get; }
    public int TdpWatts { get; }

    /// <summary>Idle power draw as a fraction of TDP, in permille.</summary>
    public int IdlePermille { get; }

    public override ComponentCategory Category => ComponentCategory.Cpu;
}

public sealed record MemorySpec : HardwareComponent
{
    public MemorySpec(string id, string name, int priceCredits, ImmutableArray<string> vendors,
        int capacityMb, int bandwidthMbps, int casLatency)
        : base(id, name, priceCredits, vendors, null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityMb, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bandwidthMbps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(casLatency, 1);
        CapacityMb = capacityMb;
        BandwidthMbps = bandwidthMbps;
        CasLatency = casLatency;
    }

    public int CapacityMb { get; }
    public int BandwidthMbps { get; }
    public int CasLatency { get; }

    public override ComponentCategory Category => ComponentCategory.Memory;
}

public sealed record StorageSpec : HardwareComponent
{
    public StorageSpec(string id, string name, int priceCredits, ImmutableArray<string> vendors, string? defaultDriver,
        StorageMedium medium, int readMbps, int dmaChannels, int accessLatencyMicros, int platterNoise)
        : base(id, name, priceCredits, vendors, defaultDriver)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readMbps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dmaChannels, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(accessLatencyMicros);
        ArgumentOutOfRangeException.ThrowIfNegative(platterNoise);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(platterNoise, 1000);
        Medium = medium;
        ReadMbps = readMbps;
        DmaChannels = dmaChannels;
        AccessLatencyMicros = accessLatencyMicros;
        PlatterNoise = platterNoise;
    }

    public StorageMedium Medium { get; }

    /// <summary>Sustained media throughput in megabytes per second.</summary>
    public int ReadMbps { get; }
    public int DmaChannels { get; }

    /// <summary>Per-command access latency (seek/queue) in microseconds.</summary>
    public int AccessLatencyMicros { get; }

    /// <summary>Acoustic intensity of mechanical activity, 0–1000.</summary>
    public int PlatterNoise { get; }

    public override ComponentCategory Category => ComponentCategory.Storage;
}

public sealed record NicSpec : HardwareComponent
{
    public NicSpec(string id, string name, int priceCredits, ImmutableArray<string> vendors, string? defaultDriver,
        int linkMbps, int ringSize, int maxBatch)
        : base(id, name, priceCredits, vendors, defaultDriver)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(linkMbps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ringSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatch, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBatch, ringSize);
        LinkMbps = linkMbps;
        RingSize = ringSize;
        MaxBatch = maxBatch;
    }

    /// <summary>Link rate in megabits per second.</summary>
    public int LinkMbps { get; }
    public int RingSize { get; }
    public int MaxBatch { get; }

    public override ComponentCategory Category => ComponentCategory.Network;
}

public sealed record BusSpec : HardwareComponent
{
    public BusSpec(string id, string name, int priceCredits, ImmutableArray<string> vendors, string? defaultDriver,
        int busWidthBits, int irqCoalesceCap, int arbitrationCycles, int interruptCycles, int interruptEnergyNanojoules)
        : base(id, name, priceCredits, vendors, defaultDriver)
    {
        if (busWidthBits < 8 || busWidthBits % 8 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(busWidthBits), "Bus width must be a positive multiple of 8 bits.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(irqCoalesceCap, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(arbitrationCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(interruptCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(interruptEnergyNanojoules);
        BusWidthBits = busWidthBits;
        IrqCoalesceCap = irqCoalesceCap;
        ArbitrationCycles = arbitrationCycles;
        InterruptCycles = interruptCycles;
        InterruptEnergyNanojoules = interruptEnergyNanojoules;
    }

    public int BusWidthBits { get; }
    public int BusWidthBytes => BusWidthBits / 8;

    /// <summary>Maximum descriptors that can be retired by a single coalesced interrupt.</summary>
    public int IrqCoalesceCap { get; }

    /// <summary>C<sub>arb</sub>: fixed bus-arbitration cycles per submission.</summary>
    public int ArbitrationCycles { get; }

    /// <summary>C<sub>irq</sub>: fixed interrupt-service cycles per raised interrupt.</summary>
    public int InterruptCycles { get; }

    /// <summary>Heat injected into the chassis by each raised interrupt.</summary>
    public int InterruptEnergyNanojoules { get; }

    public override ComponentCategory Category => ComponentCategory.Bus;
}

public sealed record GpuSpec : HardwareComponent
{
    public GpuSpec(string id, string name, int priceCredits, ImmutableArray<string> vendors, string? defaultDriver, int maxDrawBatch)
        : base(id, name, priceCredits, vendors, defaultDriver)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDrawBatch, 1);
        MaxDrawBatch = maxDrawBatch;
    }

    public int MaxDrawBatch { get; }

    public override ComponentCategory Category => ComponentCategory.Gpu;
}

public sealed record AudioSpec : HardwareComponent
{
    public AudioSpec(string id, string name, int priceCredits, ImmutableArray<string> vendors, string? defaultDriver, int bufferFrames)
        : base(id, name, priceCredits, vendors, defaultDriver)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferFrames, 1);
        BufferFrames = bufferFrames;
    }

    public int BufferFrames { get; }

    public override ComponentCategory Category => ComponentCategory.Audio;
}

/// <summary>Global cost-formula constants (Doc 3 §3.2) that are not attributes of a single component.</summary>
public sealed record CostConstants
{
    public CostConstants(int headerBytes, int cpuTransferMultiplier, int dmaTransferMultiplier, int memoryPenaltyPerCas)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(headerBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cpuTransferMultiplier, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dmaTransferMultiplier, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(memoryPenaltyPerCas);
        HeaderBytes = headerBytes;
        CpuTransferMultiplier = cpuTransferMultiplier;
        DmaTransferMultiplier = dmaTransferMultiplier;
        MemoryPenaltyPerCas = memoryPenaltyPerCas;
    }

    /// <summary>Size of one <c>DeviceCommand</c> header, used for Command Efficiency.</summary>
    public int HeaderBytes { get; }

    /// <summary>C<sub>xfer</sub>(CPU): cycles per bus word when the CPU shepherds the transfer.</summary>
    public int CpuTransferMultiplier { get; }

    /// <summary>C<sub>xfer</sub>(DMA): controller cycles per bus word when DMA is configured.</summary>
    public int DmaTransferMultiplier { get; }

    /// <summary>C<sub>mem</sub> = CAS latency × this factor, charged per cache miss.</summary>
    public int MemoryPenaltyPerCas { get; }
}
