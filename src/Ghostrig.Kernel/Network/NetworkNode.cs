using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel.Network;

/// <summary>
/// A first-class simulated host: an IP-bound filesystem with accounts and listening services (Doc 3 §5,
/// Doc 5 §3.1). Kael's own rig is a node too.
/// </summary>
public sealed class NetworkNode
{
    private readonly SortedDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly SortedDictionary<int, ServiceBinding> _services = new();

    public NetworkNode(IpAddress address, string label, VirtualFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(fileSystem);
        Address = address;
        Label = label;
        FileSystem = fileSystem;
    }

    public IpAddress Address { get; }

    /// <summary>Internal content identifier (never shown as a resolvable name).</summary>
    public string Label { get; }

    public VirtualFileSystem FileSystem { get; }

    public bool Online { get; set; } = true;

    /// <summary>DNS server this node queries when it resolves names.</summary>
    public IpAddress? Resolver { get; set; }

    /// <summary>Zone served when this node runs a DNS daemon.</summary>
    public DnsZone? Zone { get; set; }

    public IReadOnlyDictionary<string, Account> Accounts => _accounts;

    public IReadOnlyDictionary<int, ServiceBinding> Services => _services;

    public void AddAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!_accounts.TryAdd(account.UserName, account))
        {
            throw new InvalidOperationException($"Duplicate account '{account.UserName}' on {Address}.");
        }
    }

    public void Bind(ServiceBinding service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!_services.TryAdd(service.Port, service))
        {
            throw new InvalidOperationException($"Port {service.Port} is already bound on {Address}.");
        }
    }

    public override string ToString() => $"{Label} ({Address})";
}
