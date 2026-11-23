using System.Text.Json.Serialization;

namespace Ghostrig.Kernel.Persistence;

// Versioned save-game schema. Every collection is emitted in a canonical (ordinal / numeric) order so the
// serialized bytes — and therefore the state hash — are a pure function of simulation state.

public sealed record KernelSnapshot(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("seed")] ulong Seed,
    [property: JsonPropertyName("clock_ns")] long ClockNanoseconds,
    [property: JsonPropertyName("rng")] ulong[] RandomState,
    [property: JsonPropertyName("event_sequence")] long EventSequence,
    [property: JsonPropertyName("calendar_epoch_ticks")] long CalendarEpochTicks,
    [property: JsonPropertyName("billing")] BillingSnapshot Billing,
    [property: JsonPropertyName("bay")] BaySnapshot Bay,
    [property: JsonPropertyName("thermal_energy_nj")] long ThermalEnergyNanojoules,
    [property: JsonPropertyName("memory")] MemorySnapshot Memory,
    [property: JsonPropertyName("telemetry")] DeviceStatsSnapshot[] Telemetry,
    [property: JsonPropertyName("local")] LocalSnapshot Local,
    [property: JsonPropertyName("nodes")] NodeSnapshot[] Nodes);

public sealed record BillingSnapshot(
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("reference_chassis")] string? ReferenceChassis,
    [property: JsonPropertyName("reference_components")] string[]? ReferenceComponents);

public sealed record BaySnapshot(
    [property: JsonPropertyName("chassis")] string ChassisId,
    [property: JsonPropertyName("power")] string PowerState,
    [property: JsonPropertyName("slots")] SlotSnapshot[] Slots);

public sealed record SlotSnapshot(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("component")] string ComponentId);

public sealed record MemorySnapshot(
    [property: JsonPropertyName("next_id")] long NextId,
    [property: JsonPropertyName("allocations")] AllocationSnapshot[] Allocations);

public sealed record AllocationSnapshot(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("bytes")] long Bytes);

public sealed record DeviceStatsSnapshot(
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("counters")] long[] Counters);

public sealed record LocalSnapshot(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("user")] string User);

public sealed record NodeSnapshot(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("resolver")] string? Resolver,
    [property: JsonPropertyName("accounts")] AccountSnapshot[] Accounts,
    [property: JsonPropertyName("services")] ServiceSnapshot[] Services,
    [property: JsonPropertyName("dns_records")] DnsRecordSnapshot[]? DnsRecords,
    [property: JsonPropertyName("vfs")] VfsEntrySnapshot[] Vfs);

public sealed record AccountSnapshot(
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("password_hash")] string PasswordHash,
    [property: JsonPropertyName("tier")] string Tier);

public sealed record ServiceSnapshot(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("access_log")] string? AccessLog,
    [property: JsonPropertyName("anonymous")] bool Anonymous,
    [property: JsonPropertyName("banner")] string Banner);

public sealed record DnsRecordSnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("address")] string Address);

public sealed record VfsEntrySnapshot(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("acl")] AclSnapshot Acl,
    [property: JsonPropertyName("modified_ns")] long ModifiedNanoseconds,
    [property: JsonPropertyName("content")] ContentSnapshot? Content,
    [property: JsonPropertyName("baseline")] ContentSnapshot? Baseline);

public sealed record AclSnapshot(
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("entries")] AclEntrySnapshot[] Entries);

public sealed record AclEntrySnapshot(
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("perms")] int Permissions);

public sealed record ContentSnapshot(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("base64")] string? Base64,
    [property: JsonPropertyName("length")] long? Length,
    [property: JsonPropertyName("seed")] ulong? Seed,
    [property: JsonPropertyName("alphabet")] string? Alphabet);
