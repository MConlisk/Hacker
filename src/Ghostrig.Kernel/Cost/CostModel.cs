using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Time;

namespace Ghostrig.Kernel.Cost;

/// <summary>Itemized cost of a single submission (all values deterministic integers).</summary>
public sealed record CostBreakdown(
    long Interrupts,
    long ArbitrationCycles,
    long InterruptCycles,
    long TransferCycles,
    long CpuTransferCycles,
    long MemoryPenaltyCycles,
    long CpuCycles,
    long ElapsedCycles,
    SimDuration ComputeTime,
    SimDuration MediaTime,
    SimDuration CpuBusyTime,
    long HeaderBytes)
{
    /// <summary>Total simulated time the submission occupies: bus/compute cycles plus device media time.</summary>
    public SimDuration Elapsed => ComputeTime + MediaTime;
}

/// <summary>A billed submission stamped on the simulation clock.</summary>
public sealed record CostReceipt(Submission Submission, CostBreakdown Cost, SimTime Start, SimTime End);

/// <summary>
/// Pure implementation of the deterministic cost formula (Doc 3 §3.2):
/// <code>
/// C_total = N_calls·(C_arb + C_irq) + Σ (S_i / W_bus)·C_xfer(Mode) + M_miss·C_mem
/// </code>
/// Evaluated per submission (N_calls = 1). Interrupt cost generalizes C_irq to the number of interrupts
/// actually raised: a batch retires up to <c>irq_coalesce_cap</c> descriptors per interrupt.
/// </summary>
public static class CostModel
{
    public static CostBreakdown Compute(Submission submission, RigSpecification rig)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(rig);
        ValidateProtocol(submission, rig);

        var constants = rig.Constants;
        var bus = rig.Bus;

        var interrupts = CeilDiv(submission.Items, bus.IrqCoalesceCap);
        long arbitration = bus.ArbitrationCycles;
        var interruptCycles = checked(interrupts * bus.InterruptCycles);

        var words = CeilDiv(submission.PayloadBytes, bus.BusWidthBytes);
        long transfer, cpuTransfer;
        if (submission.Mode == TransferMode.Cpu)
        {
            transfer = cpuTransfer = checked(words * constants.CpuTransferMultiplier);
        }
        else
        {
            var channels = DmaChannelsFor(submission.Device, rig);
            transfer = CeilDiv(checked(words * constants.DmaTransferMultiplier), channels);
            cpuTransfer = 0;
        }

        var memoryPenalty = checked(submission.CacheMisses * rig.EffectiveCasLatency * constants.MemoryPenaltyPerCas);
        var cpuCycles = checked(arbitration + interruptCycles + cpuTransfer + memoryPenalty);
        var elapsedCycles = checked(arbitration + interruptCycles + transfer + memoryPenalty);

        return new CostBreakdown(
            interrupts,
            arbitration,
            interruptCycles,
            transfer,
            cpuTransfer,
            memoryPenalty,
            cpuCycles,
            elapsedCycles,
            CyclesToTime(elapsedCycles, rig.Cpu.ClockMhz),
            MediaTime(submission, rig),
            CyclesToTime(cpuCycles, rig.Cpu.ClockMhz),
            constants.HeaderBytes);
    }

    /// <summary>Converts CPU-clock cycles to simulated nanoseconds, rounding up.</summary>
    public static SimDuration CyclesToTime(long cycles, int clockMhz) =>
        new(CeilDiv(checked(cycles * 1000), clockMhz));

    private static void ValidateProtocol(Submission s, RigSpecification rig)
    {
        switch (s.Device)
        {
            case DeviceClass.Network:
                var nic = rig.PrimaryNic;
                if (s.Items > nic.MaxBatch)
                {
                    throw new HardwareProtocolException(
                        $"{nic.Name} accepts at most {nic.MaxBatch} packets per submission; driver sent {s.Items}.");
                }

                break;
            case DeviceClass.Gpu:
                var gpu = rig.RequireGpu();
                if (s.Items > gpu.MaxDrawBatch)
                {
                    throw new HardwareProtocolException(
                        $"{gpu.Name} accepts at most {gpu.MaxDrawBatch} primitives per draw; driver sent {s.Items}.");
                }

                break;
            case DeviceClass.Audio:
                var audio = rig.RequireAudio();
                if (s.Items > audio.BufferFrames)
                {
                    throw new HardwareProtocolException(
                        $"{audio.Name} buffers at most {audio.BufferFrames} frames; driver queued {s.Items}.");
                }

                break;
            case DeviceClass.Nvme:
                _ = NvmeDevice(rig);
                break;
        }

        if (s.Mode == TransferMode.Dma && s.Device is DeviceClass.Crypto or DeviceClass.Bus)
        {
            throw new HardwareProtocolException($"{s.Device} work cannot be offloaded to a DMA controller.");
        }
    }

    private static int DmaChannelsFor(DeviceClass device, RigSpecification rig) => device switch
    {
        DeviceClass.Storage => rig.PrimaryStorage.DmaChannels,
        DeviceClass.Nvme => NvmeDevice(rig).DmaChannels,
        _ => 1,
    };

    private static SimDuration MediaTime(Submission s, RigSpecification rig)
    {
        switch (s.Device)
        {
            case DeviceClass.Storage:
                return StorageTime(rig.PrimaryStorage, s.PayloadBytes);
            case DeviceClass.Nvme:
                return StorageTime(NvmeDevice(rig), s.PayloadBytes);
            case DeviceClass.Network:
                // link_mbps is megabits per second == bits per microsecond.
                return new SimDuration(CeilDiv(checked(s.PayloadBytes * 8 * 1000), rig.PrimaryNic.LinkMbps));
            default:
                return SimDuration.Zero;
        }
    }

    private static SimDuration StorageTime(StorageSpec storage, long bytes) =>
        // read_mbps is megabytes per second == bytes per microsecond.
        new(checked(storage.AccessLatencyMicros * 1000L + CeilDiv(checked(bytes * 1000), storage.ReadMbps)));

    private static StorageSpec NvmeDevice(RigSpecification rig) =>
        rig.Storage.FirstOrDefault(s => s.Medium == StorageMedium.Nvme)
        ?? throw new HardwareConfigurationException("No NVMe storage device is installed.");

    internal static long CeilDiv(long numerator, long denominator) =>
        numerator == 0 ? 0 : ((numerator - 1) / denominator) + 1;
}
