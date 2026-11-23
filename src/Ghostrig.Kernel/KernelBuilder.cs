using Ghostrig.Kernel.Cost;
using Ghostrig.Kernel.Determinism;
using Ghostrig.Kernel.Events;
using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Network;
using Ghostrig.Kernel.Time;
using Ghostrig.Kernel.Vfs;
using Ghostrig.Kernel.World;

namespace Ghostrig.Kernel;

/// <summary>
/// Fluent composition root for <see cref="GhostrigKernel"/> (Builder pattern, constructor injection of every
/// subsystem). Configuration is validated once in <see cref="Build"/>.
/// </summary>
public sealed class KernelBuilder
{
    /// <summary>Kael's rig on the flood-zone subnet.</summary>
    public static readonly IpAddress DefaultLocalAddress = IpAddress.Parse("10.13.37.2");

    private HardwareCatalog? _catalog;
    private string? _chassisId;
    private readonly List<string> _components = [];
    private readonly List<WorldManifest> _worlds = [];
    private ulong _seed = 0x6857_5249_4721UL;
    private IBillingProfile _billing = BareMetalBilling.Instance;
    private WorldCalendar _calendar = WorldCalendar.Default;
    private IpAddress _localAddress = DefaultLocalAddress;
    private IpAddress? _resolver;
    private string _localUser = "kael";
    private SimTime _start = SimTime.Epoch;
    private bool _powerOn;

    public KernelBuilder WithCatalog(HardwareCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        return this;
    }

    public KernelBuilder WithChassis(string chassisId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chassisId);
        _chassisId = chassisId;
        return this;
    }

    public KernelBuilder WithComponents(params string[] componentIds)
    {
        ArgumentNullException.ThrowIfNull(componentIds);
        _components.AddRange(componentIds);
        return this;
    }

    public KernelBuilder WithWorld(WorldManifest manifest)
    {
        _worlds.Add(manifest ?? throw new ArgumentNullException(nameof(manifest)));
        return this;
    }

    public KernelBuilder WithSeed(ulong seed)
    {
        _seed = seed;
        return this;
    }

    public KernelBuilder WithBilling(IBillingProfile billing)
    {
        _billing = billing ?? throw new ArgumentNullException(nameof(billing));
        return this;
    }

    public KernelBuilder WithCalendar(WorldCalendar calendar)
    {
        _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        return this;
    }

    public KernelBuilder WithLocalNode(IpAddress address, string userName = "kael")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        _localAddress = address;
        _localUser = userName;
        return this;
    }

    public KernelBuilder WithResolver(IpAddress resolver)
    {
        _resolver = resolver;
        return this;
    }

    internal KernelBuilder StartingAt(SimTime start)
    {
        _start = start;
        return this;
    }

    /// <summary>Boot the rig as the final build step.</summary>
    public KernelBuilder PoweredOn(bool powerOn = true)
    {
        _powerOn = powerOn;
        return this;
    }

    public GhostrigKernel Build()
    {
        var catalog = _catalog ?? throw new InvalidOperationException("A hardware catalog is required (WithCatalog).");
        var chassisId = _chassisId ?? throw new InvalidOperationException("A chassis is required (WithChassis).");

        var clock = new SimClock(_start);
        var events = new EventBus(clock);
        var network = new NetworkSimulator(clock, events, _calendar);

        foreach (var world in _worlds)
        {
            world.ApplyTo(network);
        }

        var local = network.CreateNode(_localAddress, "ghostrig");
        local.Resolver = _resolver;
        var user = new Principal(_localUser, PrivilegeTier.Root);
        local.FileSystem.CreateDirectory(VfsPath.Parse("/home").Combine(_localUser), Principal.System,
            Acl.ForUser(_localUser, Permissions.All), createParents: true);

        var bay = new HardwareBay(catalog, chassisId);
        foreach (var id in _components)
        {
            bay.Install(id);
        }

        var kernel = new GhostrigKernel(_seed, clock, new DeterministicRandom(_seed), catalog, bay, _billing, _calendar, events,
            network, local, user);
        if (_powerOn)
        {
            kernel.PowerOn();
        }

        return kernel;
    }
}
