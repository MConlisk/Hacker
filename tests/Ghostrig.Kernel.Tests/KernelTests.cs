using System.Collections.Immutable;
using Ghostrig.Kernel.Cost;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Events;
using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Tests.Support;
using Ghostrig.Kernel.Time;

namespace Ghostrig.Kernel.Tests;

public class KernelTests
{
    [Fact]
    public void Billing_advances_the_clock_by_exactly_the_computed_cost()
    {
        var kernel = Fixtures.Act1Kernel();
        var before = kernel.Now;
        var receipt = kernel.Bill(new Submission(DeviceClass.Network, 1, 29));

        Assert.Equal(before, receipt.Start);
        Assert.Equal(before + receipt.Cost.Elapsed, kernel.Now);
        Assert.Equal(receipt.End, kernel.Now);
    }

    [Fact]
    public void Unbatched_probes_report_the_story_command_efficiency()
    {
        var kernel = Fixtures.Act1Kernel();
        for (var i = 0; i < 100; i++)
        {
            kernel.Bill(new Submission(DeviceClass.Network, 1, 29));
        }

        var net = kernel.Telemetry.Snapshot()[DeviceClass.Network];
        Assert.Equal("31%", net.CommandEfficiency.ToString());   // "31% Command Efficiency" (Act I Scene 3)
        Assert.Equal(0, net.InterruptEfficiency!.Value.Value);   // every interrupt was a raw single descriptor
    }

    [Fact]
    public void Flushing_every_read_reports_zero_cache_efficiency()
    {
        var kernel = Fixtures.Act1Kernel();
        for (var i = 0; i < 64; i++)
        {
            kernel.Bill(new Submission(DeviceClass.Storage, 1, 1, TransferMode.Cpu, cacheMisses: 1));
        }

        Assert.Equal(0, kernel.Telemetry.Snapshot()[DeviceClass.Storage].CacheEfficiency!.Value.Value);
    }

    [Fact]
    public void Unbatched_interrupt_storms_whine_and_platter_reads_chatter_louder_than_batched_work()
    {
        var noisy = Fixtures.Act1Kernel();
        for (var i = 0; i < 2000; i++)
        {
            noisy.Bill(new Submission(DeviceClass.Storage, 1, 1, TransferMode.Cpu, cacheMisses: 1));
        }

        var quiet = Fixtures.Act1Kernel();
        quiet.Bill(new Submission(DeviceClass.Storage, 1, 2000, TransferMode.Dma, cacheHits: 1));
        quiet.Idle(noisy.Now - quiet.Now);

        var n = noisy.SampleAcoustics();
        var q = quiet.SampleAcoustics();
        Assert.True(n.CoilWhine > q.CoilWhine, $"coil whine {n.CoilWhine} vs {q.CoilWhine}");
        Assert.True(n.PlatterChatter > q.PlatterChatter, $"chatter {n.PlatterChatter} vs {q.PlatterChatter}");
    }

    [Fact]
    public void Hardware_cannot_be_swapped_while_powered()
    {
        var kernel = Fixtures.Act1Kernel();
        var ex = Assert.Throws<HardwareStateException>(() => kernel.Install("memory.ddr9-16g"));
        Assert.Equal("HW_STATE", ex.Code);
    }

    [Fact]
    public void Power_down_upgrade_reboot_expands_memory_and_publishes_events()
    {
        var kernel = Fixtures.Act1Kernel();
        var seen = new List<string>();
        using var _ = kernel.Events.Subscribe(e => seen.Add(e.EventType));

        kernel.PowerOff();
        var slot = kernel.Install("memory.ddr9-16g");
        kernel.PowerOn();

        Assert.Equal(new BaySlot(ComponentCategory.Memory, 1), slot);
        Assert.Equal((2048L + 16384) * 1024 * 1024, kernel.Memory.CapacityBytes);
        Assert.Equal(["Sys.Power", "Sys.HardwareChanged", "Sys.Power"], seen);
    }

    [Fact]
    public void Full_slots_and_missing_parts_are_configuration_faults()
    {
        var kernel = Fixtures.Act1Kernel();
        kernel.PowerOff();
        kernel.Install("memory.ddr9-16g");
        Assert.Throws<HardwareConfigurationException>(() => kernel.Install("memory.ddr9-16g"));

        kernel.Remove(new BaySlot(ComponentCategory.Cpu, 0));
        var ex = Assert.Throws<HardwareConfigurationException>(kernel.PowerOn);
        Assert.Contains("no CPU", ex.Message);
    }

    [Fact]
    public void Memory_exhaustion_raises_enomem_and_reboot_clears_ram()
    {
        var kernel = Fixtures.Act1Kernel();
        kernel.Memory.Allocate("PasswordCracker", 1L << 30);
        var ex = Assert.Throws<SimulatedOutOfMemoryException>(() => kernel.Memory.Allocate("Crawler", 2L << 30));
        Assert.Equal("ENOMEM", ex.Code);

        kernel.PowerOff();
        kernel.PowerOn();
        Assert.Equal(0, kernel.Memory.UsedBytes);
    }

    [Fact]
    public void Billing_while_powered_off_is_rejected()
    {
        var kernel = Fixtures.Act1Kernel();
        kernel.PowerOff();
        Assert.Throws<HardwareStateException>(() => kernel.Bill(new Submission(DeviceClass.Network, 1, 1)));
    }

    [Fact]
    public void Stock_rig_survives_hours_of_sustained_cpu_bound_work()
    {
        var kernel = Fixtures.Act1Kernel();
        // ~1.1 simulated hours of fully CPU-bound work (1 GB crypto submissions, ~3.3 s each).
        for (var i = 0; i < 1200; i++)
        {
            kernel.Bill(new Submission(DeviceClass.Crypto, 1, 1_000_000_000, TransferMode.Cpu, cacheMisses: 1000));
        }

        Assert.True(kernel.Now.Nanoseconds > SimDuration.FromSeconds(3600).Nanoseconds);
        Assert.Equal(PowerState.On, kernel.PowerState);
        // Steady state: ambient 26 °C + 45 W / 1.2 W/°C ≈ 63.5 °C, safely under the 95 °C cascade threshold.
        Assert.InRange(kernel.TemperatureMilliCelsius, 60_000, 66_000);
    }

    [Fact]
    public void Thermal_cascade_halts_the_kernel_and_publishes_the_fail_state_event()
    {
        var kernel = TinBoxKernel(BareMetalBilling.Instance);
        SysThermalCascadeEvent? cascade = null;
        using var _ = kernel.Events.Subscribe<SysThermalCascadeEvent>(e => cascade = e);

        var ex = Assert.Throws<KernelPanicException>(() => SustainedLoad(kernel));

        Assert.Equal("KERNEL_PANIC", ex.Code);
        Assert.Equal(PowerState.Panicked, kernel.PowerState);
        Assert.NotNull(cascade);
        Assert.Throws<KernelPanicException>(() => kernel.Bill(new Submission(DeviceClass.Network, 1, 1)));

        kernel.PowerOff();
        kernel.Idle(SimDuration.FromSeconds(600));
        kernel.PowerOn();
        Assert.Equal(PowerState.On, kernel.PowerState);
    }

    [Fact]
    public void Street_ghost_billing_never_trips_a_cascade()
    {
        var kernel = TinBoxKernel(new ReferenceRigBilling(Fixtures.ReferenceSpec()));
        SustainedLoad(kernel);
        Assert.Equal(PowerState.On, kernel.PowerState);
    }

    [Fact]
    public void Idle_time_cools_the_chassis_back_toward_ambient()
    {
        var kernel = Fixtures.Act1Kernel();
        for (var i = 0; i < 500; i++)
        {
            kernel.Bill(new Submission(DeviceClass.Crypto, 1, 1_000_000, TransferMode.Cpu));
        }

        var hot = kernel.TemperatureMilliCelsius;
        kernel.PowerOff();
        kernel.Idle(SimDuration.FromSeconds(3600));
        Assert.True(kernel.TemperatureMilliCelsius < hot);
        Assert.InRange(kernel.TemperatureMilliCelsius, 26_000, 27_000);
    }

    private static void SustainedLoad(GhostrigKernel kernel)
    {
        for (var i = 0; i < 2000; i++)
        {
            kernel.Bill(new Submission(DeviceClass.Bus, 1, 4 << 20, TransferMode.Cpu, cacheMisses: 8));
        }
    }

    /// <summary>A badly ventilated chassis that cannot dissipate the stock CPU's TDP.</summary>
    private static GhostrigKernel TinBoxKernel(IBillingProfile billing)
    {
        var tinBox = new ChassisSpec("chassis.tin-box", "Tin Box", 0, ImmutableArray<string>.Empty,
            memorySlots: 2, storageSlots: 2, networkSlots: 1,
            coolingMilliwattsPerCelsius: 200, heatCapacityJoulesPerCelsius: 2,
            ambientMilliCelsius: 30_000, cascadeMilliCelsius: 70_000, bootMilliseconds: 0);
        var catalog = new HardwareCatalog(Fixtures.Catalog.Constants, Fixtures.Catalog.Components.Append(tinBox));
        return new KernelBuilder()
            .WithCatalog(catalog)
            .WithChassis("chassis.tin-box")
            .WithComponents(Fixtures.StockRig)
            .WithBilling(billing)
            .PoweredOn()
            .Build();
    }
}
