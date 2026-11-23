using Ghostrig.Kernel.Cost;
using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Network;
using Ghostrig.Kernel.World;

namespace Ghostrig.Kernel.Tests.Support;

/// <summary>Shared builders for kernels configured from the real content files.</summary>
internal static class Fixtures
{
    public static readonly IpAddress Resolver = IpAddress.Parse("10.0.0.53");
    public static readonly IpAddress Verbena = IpAddress.Parse("10.56.14.88");

    public static readonly string[] StockRig =
    [
        "cpu.salvaged-c600", "memory.salvaged-2g", "storage.platter-500", "network.kestrel-om1",
        "bus.stock-southbridge", "gpu.novagx-2000", "audio.audiomixer",
    ];

    public static readonly string[] ReferenceRig =
    [
        "cpu.reference-x9", "memory.ddr9-16g", "storage.cryo-nvme", "network.optilink-e",
        "bus.quantumbus", "gpu.novagx-2000", "audio.audiomixer",
    ];

    private static readonly Lazy<HardwareCatalog> LazyCatalog =
        new(() => HardwareCatalog.Load(ContentPath("game", "hardware.json")));

    public static HardwareCatalog Catalog => LazyCatalog.Value;

    public static string ContentPath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "content", .. parts]);

    public static WorldManifest CoreWorld => WorldManifest.Load(ContentPath("world", "ny56-core.json"));

    public static WorldManifest VerbenaWorld => WorldManifest.Load(ContentPath("world", "act1-verbena-and-rust.json"));

    /// <summary>Kael's stock Act I rig, booted, with the Act I internet loaded.</summary>
    public static GhostrigKernel Act1Kernel(ulong seed = 42, IBillingProfile? billing = null) =>
        new KernelBuilder()
            .WithCatalog(Catalog)
            .WithChassis("chassis.subrig-salvage")
            .WithComponents(StockRig)
            .WithWorld(CoreWorld)
            .WithWorld(VerbenaWorld)
            .WithResolver(Resolver)
            .WithSeed(seed)
            .WithBilling(billing ?? BareMetalBilling.Instance)
            .PoweredOn()
            .Build();

    public static RigSpecification StockSpec() =>
        RigSpecification.From(Catalog.Get<ChassisSpec>("chassis.subrig-salvage"), StockRig.Select(Catalog.Get), Catalog.Constants);

    public static RigSpecification ReferenceSpec() =>
        RigSpecification.From(Catalog.Get<ChassisSpec>("chassis.reference-rig"), ReferenceRig.Select(Catalog.Get), Catalog.Constants);
}
