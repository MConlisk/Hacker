using Ghostrig.Kernel.Hardware;

namespace Ghostrig.Kernel.Cost;

/// <summary>
/// Mode-specific billing strategy (Doc 3 §3.4). All modes share the same kernel; only the hardware a cost is
/// billed against and whether a thermal cascade may trip differ.
/// </summary>
public interface IBillingProfile
{
    string Name { get; }

    /// <summary>Chooses the specification costs are computed against, given the rig actually installed.</summary>
    RigSpecification ResolveBillingRig(RigSpecification installed);

    bool ThermalCascadeEnabled { get; }
}

/// <summary>Bare-Metal Mode: bills 100% of computed cycles against the installed hardware.</summary>
public sealed class BareMetalBilling : IBillingProfile
{
    public static readonly BareMetalBilling Instance = new();

    public string Name => "bare-metal";
    public RigSpecification ResolveBillingRig(RigSpecification installed) => installed;
    public bool ThermalCascadeEnabled => true;
}

/// <summary>
/// Silicon &amp; Bus Mode: bills against installed hardware. Expanded bus-lane/DMA routing parameters
/// arrive with the M11 hardware-tuning work; until then the billing is identical to Bare-Metal.
/// </summary>
public sealed class SiliconAndBusBilling : IBillingProfile
{
    public static readonly SiliconAndBusBilling Instance = new();

    public string Name => "silicon-and-bus";
    public RigSpecification ResolveBillingRig(RigSpecification installed) => installed;
    public bool ThermalCascadeEnabled => true;
}

/// <summary>Street Ghost Mode: bills against a high-end reference rig and never trips a thermal cascade.</summary>
public sealed class ReferenceRigBilling : IBillingProfile
{
    public ReferenceRigBilling(RigSpecification referenceRig)
    {
        ArgumentNullException.ThrowIfNull(referenceRig);
        ReferenceRig = referenceRig;
    }

    public RigSpecification ReferenceRig { get; }
    public string Name => "street-ghost";
    public RigSpecification ResolveBillingRig(RigSpecification installed) => ReferenceRig;
    public bool ThermalCascadeEnabled => false;
}
