namespace Ghostrig.Kernel.Cost;

/// <summary>Hardware class a driver submission is routed to.</summary>
public enum DeviceClass
{
    Storage,
    Network,
    Gpu,
    Audio,

    /// <summary>Cryptographic block work executed on the CPU (no external device, cache-sensitive).</summary>
    Crypto,

    /// <summary>Raw bus / I/O controller interrupt traffic.</summary>
    Bus,

    /// <summary>NVMe submission queues (billed against an NVMe storage device).</summary>
    Nvme,
}

/// <summary>Transfer strategy (Doc 2 §4 Strategy pattern): CPU-polled vs. controller-driven DMA.</summary>
public enum TransferMode
{
    Cpu,
    Dma,
}

/// <summary>
/// One discrete hardware submission — a single <c>DeviceCommand</c> carrying one or more items
/// (packets, triangles, descriptors, frames). <c>N<sub>calls</sub></c> in the cost formula counts these.
/// </summary>
public sealed record Submission
{
    public Submission(DeviceClass device, int items, long payloadBytes, TransferMode mode = TransferMode.Cpu,
        long cacheHits = 0, long cacheMisses = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(items, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheHits);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheMisses);
        Device = device;
        Items = items;
        PayloadBytes = payloadBytes;
        Mode = mode;
        CacheHits = cacheHits;
        CacheMisses = cacheMisses;
    }

    public DeviceClass Device { get; }

    /// <summary>Number of items (packets, primitives, descriptors, frames) packed into this one command.</summary>
    public int Items { get; }

    /// <summary>S<sub>i</sub>: payload size in bytes.</summary>
    public long PayloadBytes { get; }

    public TransferMode Mode { get; }
    public long CacheHits { get; }

    /// <summary>M<sub>miss</sub>: cache or key-schedule misses incurred by this submission.</summary>
    public long CacheMisses { get; }

    public bool IsBatch => Items > 1;
}
