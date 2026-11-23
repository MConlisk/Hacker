using FsCheck;
using FsCheck.Xunit;
using Ghostrig.Kernel.Cost;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Tests.Support;
using Ghostrig.Kernel.Time;

namespace Ghostrig.Kernel.Tests;

public class CostModelTests
{
    private static readonly Hardware.RigSpecification Stock = Fixtures.StockSpec();

    [Fact]
    public void Single_submission_matches_the_documented_formula()
    {
        // 1 packet, 64 bytes, CPU mode, 2 cache misses on the stock rig.
        var s = new Submission(DeviceClass.Network, items: 1, payloadBytes: 64, TransferMode.Cpu, cacheMisses: 2);
        var cost = CostModel.Compute(s, Stock);

        const long arb = 300, irq = 400;                 // stock southbridge
        const long words = 64 / 8, xfer = words * 16;    // 64-bit bus, CPU multiplier 16
        const long mem = 2 * 18 * 60;                    // misses × CAS × penalty-per-CAS
        Assert.Equal(1, cost.Interrupts);
        Assert.Equal(arb + irq + xfer + mem, cost.CpuCycles);
        Assert.Equal(cost.CpuCycles, cost.ElapsedCycles);
        Assert.Equal(CostModel.CyclesToTime(cost.ElapsedCycles, 600), cost.ComputeTime);
        // 64 bytes over a 50 Mbps link = 512 bits / 50 bits per µs → 10.24 µs, rounded up.
        Assert.Equal(new SimDuration(10_240), cost.MediaTime);
    }

    [Fact]
    public void Batching_packets_is_cheaper_and_raises_fewer_interrupts()
    {
        const int k = 32;
        var singles = Enumerable.Range(0, k).Select(_ => CostModel.Compute(new Submission(DeviceClass.Network, 1, 29), Stock)).ToList();
        var batch = CostModel.Compute(new Submission(DeviceClass.Network, k, 29L * k), Stock);

        Assert.True(batch.Elapsed < new SimDuration(singles.Sum(c => c.Elapsed.Nanoseconds)));
        Assert.Equal(k, singles.Sum(c => c.Interrupts));
        Assert.Equal(4, batch.Interrupts); // 32 descriptors / coalesce cap 8
    }

    [Property(MaxTest = 200)]
    public Property Batching_never_costs_more_than_unbatched_submission(PositiveInt itemsSeed, PositiveInt sizeSeed, bool dma)
    {
        var items = 2 + (itemsSeed.Get % 31);          // 2..32 (within the modem's max_batch)
        var size = 1 + (sizeSeed.Get % 1500);
        var mode = dma ? TransferMode.Dma : TransferMode.Cpu;

        var unbatched = Enumerable.Range(0, items)
            .Sum(_ => CostModel.Compute(new Submission(DeviceClass.Network, 1, size, mode), Stock).Elapsed.Nanoseconds);
        var batched = CostModel.Compute(new Submission(DeviceClass.Network, items, (long)size * items, mode), Stock).Elapsed.Nanoseconds;

        return (batched <= unbatched).Label($"batched {batched} <= unbatched {unbatched}");
    }

    [Property(MaxTest = 200)]
    public Property Dma_never_uses_more_cpu_than_cpu_mode(PositiveInt bytesSeed)
    {
        var bytes = (long)bytesSeed.Get * 17;
        var cpu = CostModel.Compute(new Submission(DeviceClass.Storage, 1, bytes, TransferMode.Cpu), Stock);
        var dma = CostModel.Compute(new Submission(DeviceClass.Storage, 1, bytes, TransferMode.Dma), Stock);
        return (dma.CpuCycles <= cpu.CpuCycles && dma.CpuTransferCycles == 0).ToProperty();
    }

    [Fact]
    public void Story_timing_byte_by_byte_dictionary_takes_hours_and_dma_blocks_take_seconds()
    {
        // Act I Scene 4: a 4 GB rainbow table read by the stock StorageController (CPU mode, 1 byte per read,
        // cache flushed every read) vs. the fixed driver (DMA, 1 MiB block reads, LRU hits).
        const long dictionary = 4_000_000_000;
        const int block = 1 << 20;

        var perByte = CostModel.Compute(new Submission(DeviceClass.Storage, 1, 1, TransferMode.Cpu, cacheMisses: 1), Stock);
        var broken = new SimDuration(perByte.Elapsed.Nanoseconds * dictionary);

        var blocks = (dictionary + block - 1) / block;
        var perBlock = CostModel.Compute(new Submission(DeviceClass.Storage, 1, block, TransferMode.Dma, cacheHits: 1), Stock);
        var fixedDriver = new SimDuration(perBlock.Elapsed.Nanoseconds * blocks);

        Assert.InRange(broken.TotalSeconds, 3.5 * 3600, 5 * 3600);   // "it will take four hours"
        Assert.InRange(fixedDriver.TotalSeconds, 1, 60);              // "in seconds"
    }

    [Fact]
    public void Exceeding_the_device_batch_limit_is_a_protocol_fault()
    {
        var ex = Assert.Throws<HardwareProtocolException>(() =>
            CostModel.Compute(new Submission(DeviceClass.Network, items: 33, payloadBytes: 33 * 64), Stock));
        Assert.Equal("HW_PROTOCOL", ex.Code);
    }

    [Fact]
    public void Crypto_work_cannot_be_offloaded_to_dma() =>
        Assert.Throws<HardwareProtocolException>(() =>
            CostModel.Compute(new Submission(DeviceClass.Crypto, 1, 4096, TransferMode.Dma), Stock));

    [Fact]
    public void Key_schedule_misses_charge_memory_penalties_linearly()
    {
        var reuse = CostModel.Compute(new Submission(DeviceClass.Crypto, 16, 16 * 4096, TransferMode.Cpu, cacheHits: 16), Stock);
        var cleared = CostModel.Compute(new Submission(DeviceClass.Crypto, 16, 16 * 4096, TransferMode.Cpu, cacheMisses: 16), Stock);
        Assert.Equal(16L * 18 * 60, cleared.MemoryPenaltyCycles);
        Assert.Equal(reuse.CpuCycles + cleared.MemoryPenaltyCycles, cleared.CpuCycles);
    }

    [Fact]
    public void Nvme_submissions_require_an_nvme_device() =>
        Assert.Throws<HardwareConfigurationException>(() =>
            CostModel.Compute(new Submission(DeviceClass.Nvme, 1, 4096, TransferMode.Dma), Stock));

    [Fact]
    public void Reference_rig_is_dramatically_faster_than_stock()
    {
        var s = new Submission(DeviceClass.Storage, 1, 1 << 20, TransferMode.Cpu);
        Assert.True(CostModel.Compute(s, Fixtures.ReferenceSpec()).ComputeTime < CostModel.Compute(s, Stock).ComputeTime);
    }
}
