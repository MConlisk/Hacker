using Ghostrig.Kernel.Hardware;
using Ghostrig.Kernel.Time;

namespace Ghostrig.Kernel.Events;

/// <summary>
/// Base of every observable kernel action. The Narrative Runtime arms listeners on these (Doc 4 §1.1);
/// <see cref="EventType"/> strings are the action signatures referenced by storylet JSON.
/// </summary>
public abstract record KernelEvent
{
    /// <summary>Simulation instant the event was published (stamped by the bus).</summary>
    public SimTime At { get; init; }

    /// <summary>Monotonic publication order (stamped by the bus).</summary>
    public long Sequence { get; init; }

    public abstract string EventType { get; }
}

public sealed record FsReadEvent(string NodeAddress, string Path, string User) : KernelEvent
{
    public override string EventType => "Fs.Read";
}

public sealed record FsWriteEvent(string NodeAddress, string Path, string User, string ContentHash) : KernelEvent
{
    public override string EventType => "Fs.Write";
}

public sealed record FsAccessDeniedEvent(string NodeAddress, string Path, string User, string Operation) : KernelEvent
{
    public override string EventType => "Fs.AccessDenied";
}

public sealed record NetResolveEvent(string ClientAddress, string Hostname, string? ResolvedAddress) : KernelEvent
{
    public override string EventType => "Net.Resolve";
}

public sealed record NetConnectEvent(string SourceAddress, string DestinationAddress, int Port, string Service) : KernelEvent
{
    public override string EventType => "Net.Connect";
}

public sealed record NetLoginEvent(string SourceAddress, string DestinationAddress, int Port, string User, bool Success) : KernelEvent
{
    public override string EventType => "Net.Login";
}

public sealed record NetFtpUploadEvent(
    string SourceAddress, string DestinationAddress, string? DestinationHost, int Port, string User, string Path, string PayloadHash)
    : KernelEvent
{
    public override string EventType => "Net.FtpUpload";
}

public sealed record NetFtpDownloadEvent(
    string SourceAddress, string DestinationAddress, int Port, string User, string Path, string PayloadHash) : KernelEvent
{
    public override string EventType => "Net.FtpDownload";
}

public sealed record SysPowerEvent(PowerState State) : KernelEvent
{
    public override string EventType => "Sys.Power";
}

public sealed record SysHardwareChangedEvent(BaySlot Slot, string ComponentId, bool Installed) : KernelEvent
{
    public override string EventType => "Sys.HardwareChanged";
}

public sealed record SysThermalCascadeEvent(int TemperatureMilliCelsius) : KernelEvent
{
    public override string EventType => "Sys.ThermalCascade";
}
