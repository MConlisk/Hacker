using Ghostrig.Kernel.Cost;

namespace Ghostrig.Kernel.Telemetry;

/// <summary>A ratio expressed in permille (0–1000), or <c>null</c> when no relevant operations occurred.</summary>
public readonly record struct Permille(int Value)
{
    public static Permille? Of(long numerator, long denominator) =>
        denominator <= 0 ? null : new Permille((int)(numerator * 1000 / denominator));

    public double Percent => Value / 10.0;

    public override string ToString() => $"{Value / 10}%";
}

/// <summary>
/// Cumulative per-device execution counters (Doc 3 §3.3). Immutable; windows are computed by subtraction.
/// </summary>
public readonly record struct DriverStats(
    long Submissions,
    long Items,
    long PayloadBytes,
    long HeaderBytes,
    long CoalescedInterrupts,
    long RawInterrupts,
    long CacheHits,
    long CacheMisses,
    long CpuTransferBytes,
    long CpuCycles,
    long ElapsedNanoseconds)
{
    public long Interrupts => CoalescedInterrupts + RawInterrupts;

    /// <summary>Payload bytes as a share of payload plus <c>DeviceCommand</c> header bytes.</summary>
    public Permille? CommandEfficiency => Permille.Of(PayloadBytes, PayloadBytes + HeaderBytes);

    /// <summary>Share of interrupts that retired a coalesced batch rather than a single descriptor.</summary>
    public Permille? InterruptEfficiency => Permille.Of(CoalescedInterrupts, Interrupts);

    /// <summary>Share of cache / key-schedule lookups that hit.</summary>
    public Permille? CacheEfficiency => Permille.Of(CacheHits, CacheHits + CacheMisses);

    public DriverStats Add(Submission submission, CostBreakdown cost)
    {
        var coalesced = submission.IsBatch ? cost.Interrupts : 0;
        return this with
        {
            Submissions = Submissions + 1,
            Items = Items + submission.Items,
            PayloadBytes = PayloadBytes + submission.PayloadBytes,
            HeaderBytes = HeaderBytes + cost.HeaderBytes,
            CoalescedInterrupts = CoalescedInterrupts + coalesced,
            RawInterrupts = RawInterrupts + (cost.Interrupts - coalesced),
            CacheHits = CacheHits + submission.CacheHits,
            CacheMisses = CacheMisses + submission.CacheMisses,
            CpuTransferBytes = CpuTransferBytes + (submission.Mode == TransferMode.Cpu ? submission.PayloadBytes : 0),
            CpuCycles = CpuCycles + cost.CpuCycles,
            ElapsedNanoseconds = ElapsedNanoseconds + cost.Elapsed.Nanoseconds,
        };
    }

    public static DriverStats operator -(DriverStats a, DriverStats b) => new(
        a.Submissions - b.Submissions,
        a.Items - b.Items,
        a.PayloadBytes - b.PayloadBytes,
        a.HeaderBytes - b.HeaderBytes,
        a.CoalescedInterrupts - b.CoalescedInterrupts,
        a.RawInterrupts - b.RawInterrupts,
        a.CacheHits - b.CacheHits,
        a.CacheMisses - b.CacheMisses,
        a.CpuTransferBytes - b.CpuTransferBytes,
        a.CpuCycles - b.CpuCycles,
        a.ElapsedNanoseconds - b.ElapsedNanoseconds);
}
