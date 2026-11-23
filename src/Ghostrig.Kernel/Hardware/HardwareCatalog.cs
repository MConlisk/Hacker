using System.Collections.Immutable;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Json;

namespace Ghostrig.Kernel.Hardware;

/// <summary>
/// The authoritative, validated set of hardware definitions loaded from <c>content/game/hardware.json</c>.
/// </summary>
public sealed class HardwareCatalog
{
    public const int SupportedSchemaVersion = 1;

    private readonly ImmutableDictionary<string, HardwareComponent> _components;

    public HardwareCatalog(CostConstants constants, IEnumerable<HardwareComponent> components)
    {
        ArgumentNullException.ThrowIfNull(constants);
        ArgumentNullException.ThrowIfNull(components);
        var builder = ImmutableDictionary.CreateBuilder<string, HardwareComponent>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            if (!builder.TryAdd(component.Id, component))
            {
                throw new ContentValidationException("hardware catalog", $"duplicate component id '{component.Id}'.");
            }
        }

        Constants = constants;
        _components = builder.ToImmutable();
    }

    public CostConstants Constants { get; }

    public IEnumerable<HardwareComponent> Components => _components.Values.OrderBy(c => c.Id, StringComparer.Ordinal);

    public HardwareComponent Get(string id) =>
        _components.TryGetValue(id, out var component)
            ? component
            : throw new HardwareConfigurationException($"Unknown hardware component '{id}'.");

    public T Get<T>(string id) where T : HardwareComponent =>
        Get(id) as T ?? throw new HardwareConfigurationException($"Component '{id}' is not a {typeof(T).Name}.");

    public bool Contains(string id) => _components.ContainsKey(id);

    /// <summary>Parses and validates a catalog document.</summary>
    public static HardwareCatalog Parse(string json, string source = "hardware.json")
    {
        using var document = JsonFields.Parse(json, source);
        var root = new JsonFields(document.RootElement, source, "$");
        var version = root.RequireInt32("schema_version");
        if (version != SupportedSchemaVersion)
        {
            throw root.Error($"unsupported schema_version {version}; expected {SupportedSchemaVersion}.");
        }

        var c = root.RequireObject("cost_constants");
        var constants = Guard(c, () => new CostConstants(
            c.RequireInt32("header_bytes", 1),
            c.RequireInt32("cpu_transfer_multiplier", 1),
            c.RequireInt32("dma_transfer_multiplier", 1),
            c.RequireInt32("memory_penalty_per_cas", 0)));

        var components = root.RequireObjectArray("components").Select(ParseComponent).ToList();
        return new HardwareCatalog(constants, components);
    }

    public static HardwareCatalog Load(string path) => Parse(File.ReadAllText(path), path);

    private static HardwareComponent ParseComponent(JsonFields f)
    {
        var id = f.RequireString("id");
        var name = f.RequireString("name");
        var price = f.RequireInt32("price_credits", 0);
        var vendors = f.OptionalStringArray("vendors").ToImmutableArray();
        var driver = f.OptionalString("default_driver");
        var category = f.RequireString("category");

        return Guard(f, () => category switch
        {
            "chassis" => (HardwareComponent)new ChassisSpec(id, name, price, vendors,
                f.RequireInt32("memory_slots", 1), f.RequireInt32("storage_slots", 1), f.RequireInt32("network_slots", 0),
                f.RequireInt32("cooling_mw_per_c", 1), f.RequireInt32("heat_capacity_j_per_c", 1),
                f.RequireInt32("ambient_mc"), f.RequireInt32("cascade_mc"), f.RequireInt32("boot_ms", 0)),
            "cpu" => new CpuSpec(id, name, price, vendors,
                f.RequireInt32("clock_mhz", 1), f.RequireInt32("cores", 1),
                (int)f.RequireFixedPoint("ipc", 1000, 1, 1_000_000), f.RequireInt32("tdp_watts", 1),
                f.RequireInt32("idle_permille", 0, 1000)),
            "memory" => new MemorySpec(id, name, price, vendors,
                f.RequireInt32("capacity_mb", 1), (int)f.RequireFixedPoint("bandwidth_gbps", 1000, 1, int.MaxValue),
                f.RequireInt32("cas_latency", 1)),
            "storage" => new StorageSpec(id, name, price, vendors, driver,
                ParseMedium(f), f.RequireInt32("read_mbps", 1), f.RequireInt32("dma_channels", 1),
                f.RequireInt32("access_latency_us", 0), f.RequireInt32("platter_noise", 0, 1000)),
            "network" => new NicSpec(id, name, price, vendors, driver,
                f.RequireInt32("link_mbps", 1), f.RequireInt32("ring_size", 1), f.RequireInt32("max_batch", 1)),
            "bus" => new BusSpec(id, name, price, vendors, driver,
                f.RequireInt32("bus_width_bits", 8), f.RequireInt32("irq_coalesce_cap", 1),
                f.RequireInt32("arbitration_cycles", 0), f.RequireInt32("interrupt_cycles", 0),
                f.RequireInt32("interrupt_energy_nj", 0)),
            "gpu" => new GpuSpec(id, name, price, vendors, driver, f.RequireInt32("max_draw_batch", 1)),
            "audio" => new AudioSpec(id, name, price, vendors, driver, f.RequireInt32("buffer_frames", 1)),
            _ => throw f.Error($"unknown category '{category}'."),
        });
    }

    private static StorageMedium ParseMedium(JsonFields f) => f.RequireString("medium") switch
    {
        "platter" => StorageMedium.Platter,
        "solid" => StorageMedium.Solid,
        "nvme" => StorageMedium.Nvme,
        var other => throw f.Error($"unknown storage medium '{other}'."),
    };

    /// <summary>Converts constructor argument validation into content validation errors that name the JSON path.</summary>
    private static T Guard<T>(JsonFields f, Func<T> create)
    {
        try
        {
            return create();
        }
        catch (ArgumentException ex)
        {
            throw f.Error(ex.Message);
        }
    }
}
