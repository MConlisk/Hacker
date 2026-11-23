using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ghostrig.Kernel.Cost;
using Ghostrig.Kernel.Determinism;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Events;
using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Memory;
using Ghostrig.Kernel.Network;
using Ghostrig.Kernel.Telemetry;
using Ghostrig.Kernel.Time;
using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel.Persistence;

/// <summary>
/// Captures, serializes, hashes and restores complete kernel state. The state hash is the determinism oracle:
/// replaying the same inputs from the same snapshot must reproduce it bit-for-bit (Doc 3 §2.3).
/// </summary>
/// <remarks>
/// Transient presentation state (the acoustic sampling window and event subscriptions) is deliberately not saved;
/// subscribers re-attach after a load.
/// </remarks>
public static class KernelPersistence
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    #region Capture

    public static KernelSnapshot Capture(GhostrigKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        var rng = kernel.Random.State;
        var (nextId, live) = kernel.Memory.Export();

        return new KernelSnapshot(
            SchemaVersion,
            kernel.Seed,
            kernel.Clock.Now.Nanoseconds,
            [rng.S0, rng.S1, rng.S2, rng.S3],
            kernel.Events.PublishedCount,
            kernel.Calendar.EpochUtc.Ticks,
            CaptureBilling(kernel.Billing),
            new BaySnapshot(
                kernel.Bay.Chassis.Id,
                kernel.Bay.PowerState.ToString(),
                kernel.Bay.Installed.Select(kv => new SlotSnapshot(kv.Key.Category.ToString(), kv.Key.Index, kv.Value.Id)).ToArray()),
            kernel.Thermal.StoredEnergyNanojoules,
            new MemorySnapshot(nextId, live.OrderBy(a => a.Handle.Id).Select(a => new AllocationSnapshot(a.Handle.Id, a.Owner, a.Bytes)).ToArray()),
            kernel.Telemetry.Snapshot().Devices.Select(kv => new DeviceStatsSnapshot(kv.Key.ToString(), Counters(kv.Value))).ToArray(),
            new LocalSnapshot(kernel.LocalNode.Address.ToString(), kernel.LocalUser.UserName),
            kernel.Network.Nodes.Select(CaptureNode).ToArray());
    }

    private static BillingSnapshot CaptureBilling(IBillingProfile billing) => billing switch
    {
        ReferenceRigBilling r => new BillingSnapshot(r.Name, r.ReferenceRig.Chassis.Id, ReferenceComponentIds(r.ReferenceRig)),
        _ => new BillingSnapshot(billing.Name, null, null),
    };

    private static string[] ReferenceComponentIds(RigSpecification rig)
    {
        var parts = new List<HardwareComponent> { rig.Cpu, rig.Bus };
        parts.AddRange(rig.Memory);
        parts.AddRange(rig.Storage);
        parts.AddRange(rig.Network);
        if (rig.Gpu is not null) parts.Add(rig.Gpu);
        if (rig.Audio is not null) parts.Add(rig.Audio);
        return parts.Select(p => p.Id).ToArray();
    }

    private static NodeSnapshot CaptureNode(NetworkNode node) => new(
        node.Address.ToString(),
        node.Label,
        node.Online,
        node.Resolver?.ToString(),
        node.Accounts.Values.Select(a => new AccountSnapshot(a.UserName, a.PasswordHash, a.Tier.ToString())).ToArray(),
        node.Services.Values.Select(s => new ServiceSnapshot(s.Port, s.Kind.ToString(), s.Root.Value, s.AccessLog?.Value, s.AllowAnonymous, s.Banner)).ToArray(),
        node.Zone?.Records.Select(r => new DnsRecordSnapshot(r.Key, r.Value.ToString())).ToArray(),
        node.FileSystem.Export().Select(r => new VfsEntrySnapshot(
            r.Path.Value,
            r.Kind.ToString(),
            CaptureAcl(r.Acl),
            r.Modified.Nanoseconds,
            r.Content is null ? null : CaptureContent(r.Content),
            r.Baseline is null ? null : CaptureContent(r.Baseline))).ToArray());

    private static AclSnapshot CaptureAcl(Acl acl) => new(
        acl.Owner,
        acl.Group,
        acl.Entries.Select(e => e.Subject switch
        {
            AclSubject.TierAtLeast t => new AclEntrySnapshot(t.Tier.ToString(), null, (int)e.Permissions),
            AclSubject.User u => new AclEntrySnapshot(null, u.UserName, (int)e.Permissions),
            _ => throw new InvalidOperationException($"Unknown ACL subject {e.Subject}."),
        }).ToArray());

    private static ContentSnapshot CaptureContent(FileContent content) => content switch
    {
        InlineFileContent inline => new ContentSnapshot("inline", Convert.ToBase64String(inline.Span), null, null, null),
        SyntheticFileContent s => new ContentSnapshot("synthetic", null, s.Length, s.Seed, s.Alphabet.ToString()),
        _ => throw new InvalidOperationException($"Unsupported content type {content.GetType().Name}."),
    };

    private static long[] Counters(DriverStats s) =>
    [
        s.Submissions, s.Items, s.PayloadBytes, s.HeaderBytes, s.CoalescedInterrupts, s.RawInterrupts,
        s.CacheHits, s.CacheMisses, s.CpuTransferBytes, s.CpuCycles, s.ElapsedNanoseconds,
    ];

    #endregion

    #region Serialization and hashing

    public static byte[] Serialize(KernelSnapshot snapshot) => JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);

    public static KernelSnapshot Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        KernelSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<KernelSnapshot>(utf8Json, Options);
        }
        catch (JsonException ex)
        {
            throw new ContentValidationException("save", $"corrupt save data: {ex.Message}");
        }

        if (snapshot is null)
        {
            throw new ContentValidationException("save", "empty save data.");
        }

        if (snapshot.SchemaVersion != SchemaVersion)
        {
            throw new ContentValidationException("save", $"unsupported save schema {snapshot.SchemaVersion}; expected {SchemaVersion}.");
        }

        return snapshot;
    }

    /// <summary>SHA-256 over the canonical serialized snapshot.</summary>
    public static string ComputeStateHash(GhostrigKernel kernel) =>
        Convert.ToHexString(SHA256.HashData(Serialize(Capture(kernel)))).ToLowerInvariant();

    #endregion

    #region Restore

    /// <summary>Rebuilds a kernel from a snapshot. The catalog must contain every referenced component.</summary>
    public static GhostrigKernel Restore(KernelSnapshot snapshot, HardwareCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);

        var clock = new SimClock(new SimTime(snapshot.ClockNanoseconds));
        var events = new EventBus(clock);
        events.RestoreSequence(snapshot.EventSequence);
        var calendar = new WorldCalendar(new DateTime(snapshot.CalendarEpochTicks, DateTimeKind.Utc));
        var network = new NetworkSimulator(clock, events, calendar);

        foreach (var n in snapshot.Nodes)
        {
            network.AddNode(RestoreNode(n, clock));
        }

        var bay = new HardwareBay(catalog, snapshot.Bay.ChassisId);
        foreach (var slot in snapshot.Bay.Slots)
        {
            bay.InstallAt(new BaySlot(Enum.Parse<ComponentCategory>(slot.Category), slot.Index), slot.ComponentId);
        }

        var power = Enum.Parse<PowerState>(snapshot.Bay.PowerState);
        var rng = snapshot.RandomState;
        if (rng.Length != 4)
        {
            throw new ContentValidationException("save", "random state must contain four words.");
        }

        var local = network.GetNode(IpAddress.Parse(snapshot.Local.Address));
        var kernel = new GhostrigKernel(
            snapshot.Seed, clock, DeterministicRandom.FromState(new RandomState(rng[0], rng[1], rng[2], rng[3])),
            catalog, bay, RestoreBilling(snapshot.Billing, catalog), calendar, events, network, local,
            new Principal(snapshot.Local.User, PrivilegeTier.Root), snapshot.ThermalEnergyNanojoules);

        // Restore power state and the boot-time rig without re-running the boot sequence.
        bay.RestorePowerState(power);
        RigSpecification? rig = power == PowerState.Off ? null : bay.Describe();
        kernel.RestoreRuntime(rig);
        kernel.Memory.Import(rig?.MemoryCapacityBytes ?? 0, snapshot.Memory.NextId,
            snapshot.Memory.Allocations.Select(a => new Allocation(new AllocationHandle(a.Id), a.Owner, a.Bytes)));

        kernel.Telemetry.Restore(new TelemetrySnapshot(snapshot.Telemetry.ToImmutableSortedDictionary(
            t => Enum.Parse<DeviceClass>(t.Device), t => RestoreStats(t.Counters))));
        return kernel;
    }

    private static IBillingProfile RestoreBilling(BillingSnapshot billing, HardwareCatalog catalog) => billing.Profile switch
    {
        "bare-metal" => BareMetalBilling.Instance,
        "silicon-and-bus" => SiliconAndBusBilling.Instance,
        "street-ghost" => new ReferenceRigBilling(RigSpecification.From(
            catalog.Get<ChassisSpec>(billing.ReferenceChassis ?? throw new ContentValidationException("save", "missing reference chassis.")),
            (billing.ReferenceComponents ?? []).Select(catalog.Get),
            catalog.Constants)),
        var other => throw new ContentValidationException("save", $"unknown billing profile '{other}'."),
    };

    private static NetworkNode RestoreNode(NodeSnapshot n, SimClock clock)
    {
        var records = n.Vfs;
        if (records.Length == 0 || records[0].Path != "/")
        {
            throw new ContentValidationException("save", $"node {n.Address} is missing its filesystem root.");
        }

        var fs = new VirtualFileSystem(clock, RestoreAcl(records[0].Acl));
        foreach (var r in records)
        {
            fs.Import(new VfsRecord(
                VfsPath.Parse(r.Path),
                Enum.Parse<VfsEntryKind>(r.Kind),
                RestoreAcl(r.Acl),
                new SimTime(r.ModifiedNanoseconds),
                r.Content is null ? null : RestoreContent(r.Content),
                r.Baseline is null ? null : RestoreContent(r.Baseline)));
        }

        var node = new NetworkNode(IpAddress.Parse(n.Address), n.Label, fs)
        {
            Online = n.Online,
            Resolver = n.Resolver is null ? null : IpAddress.Parse(n.Resolver),
        };

        foreach (var a in n.Accounts)
        {
            node.AddAccount(new Account(a.User, a.PasswordHash, Enum.Parse<PrivilegeTier>(a.Tier)));
        }

        foreach (var s in n.Services)
        {
            node.Bind(new ServiceBinding(s.Port, Enum.Parse<ServiceKind>(s.Kind), VfsPath.Parse(s.Root),
                s.AccessLog is null ? (VfsPath?)null : VfsPath.Parse(s.AccessLog), s.Anonymous, s.Banner));
        }

        if (n.DnsRecords is not null)
        {
            node.Zone = new DnsZone(n.DnsRecords.Select(r => new KeyValuePair<string, IpAddress>(r.Name, IpAddress.Parse(r.Address))));
        }

        return node;
    }

    private static Acl RestoreAcl(AclSnapshot a) => new(
        a.Owner,
        a.Group,
        a.Entries.Select(e => new AclEntry(
            e.Tier is not null ? new AclSubject.TierAtLeast(Enum.Parse<PrivilegeTier>(e.Tier))
            : new AclSubject.User(e.User ?? throw new ContentValidationException("save", "ACL entry without subject.")),
            (Permissions)e.Permissions)).ToImmutableArray());

    private static FileContent RestoreContent(ContentSnapshot c) => c.Kind switch
    {
        "inline" => FileContent.FromBytes(Convert.FromBase64String(c.Base64 ?? string.Empty)),
        "synthetic" => new SyntheticFileContent(c.Length ?? 0, c.Seed ?? 0, Enum.Parse<SyntheticAlphabet>(c.Alphabet ?? nameof(SyntheticAlphabet.Binary))),
        var other => throw new ContentValidationException("save", $"unknown content kind '{other}'."),
    };

    private static DriverStats RestoreStats(long[] c) => c.Length == 11
        ? new DriverStats(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10])
        : throw new ContentValidationException("save", "telemetry counters are malformed.");

    #endregion
}
