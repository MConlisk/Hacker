using Ghostrig.Kernel.Errors;

namespace Ghostrig.Kernel.Memory;

/// <summary>Handle to a simulated RAM allocation.</summary>
public readonly record struct AllocationHandle(long Id);

/// <summary>A live allocation, owned by a process or subsystem tag.</summary>
public sealed record Allocation(AllocationHandle Handle, string Owner, long Bytes);

/// <summary>
/// Tracks simulated RAM against the installed module capacity. Applications load dictionaries, graphs and
/// buffers here; exceeding capacity raises <c>ENOMEM</c> instead of silently degrading (Doc 2 §4.1).
/// </summary>
public sealed class MemoryManager
{
    private readonly SortedDictionary<long, Allocation> _allocations = new();
    private long _nextId = 1;

    public long CapacityBytes { get; private set; }

    public long UsedBytes { get; private set; }

    public long FreeBytes => CapacityBytes - UsedBytes;

    public IEnumerable<Allocation> Allocations => _allocations.Values;

    public AllocationHandle Allocate(string owner, long bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (bytes > FreeBytes)
        {
            throw new SimulatedOutOfMemoryException(
                $"{owner} requested {bytes:N0} bytes but only {FreeBytes:N0} of {CapacityBytes:N0} are free.");
        }

        var handle = new AllocationHandle(_nextId++);
        _allocations.Add(handle.Id, new Allocation(handle, owner, bytes));
        UsedBytes += bytes;
        return handle;
    }

    public void Free(AllocationHandle handle)
    {
        if (!_allocations.Remove(handle.Id, out var allocation))
        {
            throw new InvalidOperationException($"Allocation {handle.Id} is not live (double free?).");
        }

        UsedBytes -= allocation.Bytes;
    }

    /// <summary>Called at boot: memory is volatile, so every allocation is lost and capacity is re-read.</summary>
    internal void Reset(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        _allocations.Clear();
        UsedBytes = 0;
        CapacityBytes = capacityBytes;
    }

    internal (long NextId, IReadOnlyCollection<Allocation> Live) Export() => (_nextId, _allocations.Values.ToList());

    internal void Import(long capacityBytes, long nextId, IEnumerable<Allocation> live)
    {
        Reset(capacityBytes);
        _nextId = nextId;
        foreach (var a in live)
        {
            _allocations.Add(a.Handle.Id, a);
            UsedBytes += a.Bytes;
        }
    }
}
