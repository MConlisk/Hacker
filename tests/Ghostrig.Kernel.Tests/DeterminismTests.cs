using FsCheck;
using FsCheck.Xunit;
using Ghostrig.Kernel.Cost;
using Ghostrig.Kernel.Determinism;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Events;
using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Persistence;
using Ghostrig.Kernel.Tests.Support;
using Ghostrig.Kernel.Time;
using Ghostrig.Kernel.Vfs;
using Ghostrig.Kernel.World;

namespace Ghostrig.Kernel.Tests;

public class DeterminismTests
{
    /// <summary>A representative input script touching every subsystem.</summary>
    private static void Script(GhostrigKernel kernel)
    {
        for (var i = 0; i < 50; i++)
        {
            kernel.Bill(new Submission(DeviceClass.Network, 1 + (int)kernel.Random.NextInt64(0, 32), 29 * 32));
            kernel.Bill(new Submission(DeviceClass.Storage, 1, 4096, i % 2 == 0 ? TransferMode.Cpu : TransferMode.Dma, cacheMisses: i % 3));
        }

        var ip = kernel.Network.Resolve(kernel.LocalNode, "verbena-and-rust.ny56");
        using (var chen = kernel.Network.Connect(kernel.LocalNode, ip, 2121, "verbena-and-rust.ny56").Login("v.chen", "orchidroots56"))
        {
            chen.Write("/srv/ftp/storefront/kael.txt", FileContent.FromText($"tick {kernel.Now.Nanoseconds}"));
        }

        kernel.Memory.Allocate("Crawler", 64L << 20);
        kernel.Idle(SimDuration.FromSeconds(3));
    }

    [Fact]
    public void Identical_inputs_produce_identical_state_hashes()
    {
        var a = Fixtures.Act1Kernel(seed: 99);
        var b = Fixtures.Act1Kernel(seed: 99);
        Script(a);
        Script(b);
        Assert.Equal(KernelPersistence.ComputeStateHash(a), KernelPersistence.ComputeStateHash(b));
    }

    [Fact]
    public void Different_seeds_diverge()
    {
        var a = Fixtures.Act1Kernel(seed: 1);
        var b = Fixtures.Act1Kernel(seed: 2);
        Script(a);
        Script(b);
        Assert.NotEqual(KernelPersistence.ComputeStateHash(a), KernelPersistence.ComputeStateHash(b));
    }

    [Fact]
    public void Save_load_round_trip_preserves_state_and_replays_identically()
    {
        var original = Fixtures.Act1Kernel(seed: 7);
        Script(original);

        var bytes = KernelPersistence.Serialize(KernelPersistence.Capture(original));
        var restored = KernelPersistence.Restore(KernelPersistence.Deserialize(bytes), Fixtures.Catalog);

        Assert.Equal(KernelPersistence.ComputeStateHash(original), KernelPersistence.ComputeStateHash(restored));
        Assert.Equal(PowerState.On, restored.PowerState);
        Assert.Equal(original.Memory.UsedBytes, restored.Memory.UsedBytes);

        // Continue both from the same point: they must stay in lock-step.
        Script(original);
        Script(restored);
        Assert.Equal(KernelPersistence.ComputeStateHash(original), KernelPersistence.ComputeStateHash(restored));
    }

    [Fact]
    public void Street_ghost_billing_survives_a_round_trip()
    {
        var kernel = Fixtures.Act1Kernel(billing: new ReferenceRigBilling(Fixtures.ReferenceSpec()));
        var restored = KernelPersistence.Restore(
            KernelPersistence.Deserialize(KernelPersistence.Serialize(KernelPersistence.Capture(kernel))), Fixtures.Catalog);
        Assert.Equal("street-ghost", restored.Billing.Name);
        Assert.False(restored.Billing.ThermalCascadeEnabled);
        Assert.Equal(KernelPersistence.ComputeStateHash(kernel), KernelPersistence.ComputeStateHash(restored));
    }

    [Fact]
    public void Corrupt_saves_are_rejected_explicitly()
    {
        Assert.Equal("CONTENT", Assert.Throws<ContentValidationException>(() => KernelPersistence.Deserialize("{"u8)).Code);
        Assert.Throws<ContentValidationException>(() => KernelPersistence.Deserialize("""{"schema_version": 99}"""u8));
    }

    [Property(MaxTest = 200)]
    public Property Random_ranges_are_respected(ulong seed, int a, int b)
    {
        var (lo, hi) = a == b ? (a, a + 1L) : (Math.Min(a, b), (long)Math.Max(a, b));
        var rng = new DeterministicRandom(seed);
        return Enumerable.Range(0, 20).Select(_ => rng.NextInt64(lo, hi)).All(v => v >= lo && v < hi).ToProperty();
    }

    [Fact]
    public void Random_state_restores_mid_stream()
    {
        var rng = new DeterministicRandom(123);
        rng.NextUInt64();
        var resumed = DeterministicRandom.FromState(rng.State);
        Assert.Equal(rng.NextUInt64(), resumed.NextUInt64());
    }
}

public class EventBusTests
{
    private sealed record Ping(int N) : KernelEvent
    {
        public override string EventType => "Test.Ping";
    }

    [Fact]
    public void Events_are_stamped_and_reentrant_publishes_are_queued_breadth_first()
    {
        var clock = new SimClock();
        var bus = new EventBus(clock);
        var order = new List<string>();
        bus.Subscribe<Ping>(p =>
        {
            order.Add($"a{p.N}");
            if (p.N == 0)
            {
                bus.Publish(new Ping(1));
            }
        });
        bus.Subscribe<Ping>(p => order.Add($"b{p.N}"));

        clock.Advance(SimDuration.FromSeconds(1));
        bus.Publish(new Ping(0));

        Assert.Equal(["a0", "b0", "a1", "b1"], order);
        Assert.Equal(2, bus.PublishedCount);
    }

    [Fact]
    public void Failing_subscribers_do_not_starve_others()
    {
        var bus = new EventBus(new SimClock());
        var delivered = 0;
        bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe(_ => delivered++);
        Assert.Throws<AggregateException>(() => bus.Publish(new Ping(0)));
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void Disposed_subscriptions_stop_receiving()
    {
        var bus = new EventBus(new SimClock());
        var count = 0;
        var sub = bus.Subscribe(_ => count++);
        bus.Publish(new Ping(0));
        sub.Dispose();
        bus.Publish(new Ping(1));
        Assert.Equal(1, count);
    }
}

public class ContentValidationTests
{
    [Fact]
    public void Shipped_content_loads()
    {
        Assert.True(Fixtures.Catalog.Contains("bus.quantumbus"));
        Assert.Single(Fixtures.VerbenaWorld.Hosts);
    }

    [Theory]
    [InlineData("""{"schema_version":1,"cost_constants":{"header_bytes":64,"cpu_transfer_multiplier":16,"dma_transfer_multiplier":1,"memory_penalty_per_cas":60},"components":[{"category":"flux","id":"x","name":"x","price_credits":0}]}""", "unknown category")]
    [InlineData("""{"schema_version":1,"cost_constants":{"header_bytes":64,"cpu_transfer_multiplier":16,"dma_transfer_multiplier":1,"memory_penalty_per_cas":60},"components":[{"category":"bus","id":"b","name":"b","price_credits":0,"bus_width_bits":12,"irq_coalesce_cap":1,"arbitration_cycles":1,"interrupt_cycles":1,"interrupt_energy_nj":1}]}""", "multiple of 8")]
    [InlineData("""{"schema_version":2}""", "schema_version")]
    public void Catalog_errors_name_the_problem(string json, string expected)
    {
        var ex = Assert.Throws<ContentValidationException>(() => HardwareCatalog.Parse(json, "test.json"));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("test.json", ex.Message);
    }

    [Fact]
    public void World_files_with_unsupported_types_are_rejected()
    {
        const string json = """{"schema_version":1,"hosts":[{"address":"10.1.1.1","label":"x","files":[{"path":"/payload.exe","text":"x"}]}]}""";
        var ex = Assert.Throws<ContentValidationException>(() => WorldManifest.Parse(json, "w.json"));
        Assert.Contains("$.hosts[0].files[0]", ex.Message);
    }

    [Fact]
    public void Underscored_hostnames_are_invalid_dns()
    {
        const string json = """{"schema_version":1,"hosts":[{"address":"10.1.1.1","label":"x","services":[{"port":53,"kind":"dns"}],"dns_records":[{"name":"vex_drop.lotus.ny56","address":"10.1.1.2"}]}]}""";
        Assert.Throws<ContentValidationException>(() => WorldManifest.Parse(json, "w.json"));
    }
}
