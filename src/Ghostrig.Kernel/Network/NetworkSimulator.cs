using System.Text;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Events;
using Ghostrig.Kernel.Time;
using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel.Network;

/// <summary>
/// The in-game internet (Doc 5 §3): every node is reachable through the same DNS, connection and session
/// semantics. The simulator is functional only — it performs no cost billing; the <c>Net</c> system library
/// bills transfers through the active CScript driver (M3).
/// </summary>
public sealed class NetworkSimulator
{
    public const int DnsPort = 53;

    private readonly SortedDictionary<IpAddress, NetworkNode> _nodes = new();
    private readonly SimClock _clock;
    private readonly IEventPublisher _events;

    public NetworkSimulator(SimClock clock, IEventPublisher events, WorldCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(calendar);
        _clock = clock;
        _events = events;
        Calendar = calendar;
    }

    public WorldCalendar Calendar { get; }

    public IEnumerable<NetworkNode> Nodes => _nodes.Values;

    internal SimClock Clock => _clock;

    internal IEventPublisher Events => _events;

    /// <summary>Creates and registers a node with an empty filesystem.</summary>
    public NetworkNode CreateNode(IpAddress address, string label, Acl? rootAcl = null)
    {
        var node = new NetworkNode(address, label, new VirtualFileSystem(_clock, rootAcl ?? VirtualFileSystem.DefaultRootAcl));
        AddNode(node);
        return node;
    }

    public void AddNode(NetworkNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.TryAdd(node.Address, node))
        {
            throw new InvalidOperationException($"Address {node.Address} is already assigned to {_nodes[node.Address]}.");
        }
    }

    public NetworkNode GetNode(IpAddress address) =>
        _nodes.TryGetValue(address, out var node) ? node : throw new HostUnreachableException(address.ToString());

    public bool TryGetNode(IpAddress address, out NetworkNode node) => _nodes.TryGetValue(address, out node!);

    /// <summary>Resolves a hostname through the client's configured DNS server (never by string matching in apps).</summary>
    public IpAddress Resolve(NetworkNode client, string hostname)
    {
        ArgumentNullException.ThrowIfNull(client);
        string normalized;
        try
        {
            normalized = DnsZone.Normalize(hostname);
        }
        catch (ArgumentException)
        {
            _events.Publish(new NetResolveEvent(client.Address.ToString(), hostname, null));
            throw new DnsResolutionException(hostname);
        }

        var zone = ResolverZone(client);
        if (!zone.TryResolve(normalized, out var address))
        {
            _events.Publish(new NetResolveEvent(client.Address.ToString(), normalized, null));
            throw new DnsResolutionException(normalized);
        }

        _events.Publish(new NetResolveEvent(client.Address.ToString(), normalized, address.ToString()));
        return address;
    }

    /// <summary>Reverse lookup: hostnames registered for an address (empty for dark-subnet hosts).</summary>
    public IReadOnlyList<string> ReverseLookup(NetworkNode client, IpAddress address) =>
        ResolverZone(client).ReverseLookup(address);

    /// <summary>
    /// Opens a connection to a listening service. <c>hostname</c> is the DNS name the client resolved, if any;
    /// it is recorded in upload events so narrative listeners can match on the destination host.
    /// </summary>
    public Connection Connect(NetworkNode client, IpAddress destination, int port, string? hostname = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!client.Online)
        {
            throw new HostUnreachableException(destination.ToString());
        }

        if (!_nodes.TryGetValue(destination, out var server) || !server.Online)
        {
            throw new HostUnreachableException(destination.ToString());
        }

        if (!server.Services.TryGetValue(port, out var service))
        {
            throw new ConnectionRefusedException(destination.ToString(), port);
        }

        _events.Publish(new NetConnectEvent(client.Address.ToString(), destination.ToString(), port, service.Kind.ToString()));
        return new Connection(this, client, server, service, hostname is null ? null : DnsZone.Normalize(hostname));
    }

    /// <summary>Appends a structured forensic entry to a service's access log (Doc 3 §5.2).</summary>
    internal void Log(NetworkNode server, ServiceBinding service, string user, IpAddress source, string action)
    {
        if (service.AccessLog is not { } path)
        {
            return;
        }

        var line = $"[{Calendar.FormatTimestamp(_clock.Now)}] [{user}] [{source}] [{action}]\n";
        if (!server.FileSystem.Exists(path.Parent))
        {
            server.FileSystem.CreateDirectory(path.Parent, Principal.System, createParents: true);
        }

        server.FileSystem.Append(path, Principal.System, Encoding.UTF8.GetBytes(line));
    }

    private DnsZone ResolverZone(NetworkNode client)
    {
        if (client.Resolver is not { } resolverAddress)
        {
            throw new NetworkProtocolException($"{client.Address} has no DNS resolver configured.");
        }

        if (!_nodes.TryGetValue(resolverAddress, out var resolver) || !resolver.Online
            || !resolver.Services.TryGetValue(DnsPort, out var dns) || dns.Kind != ServiceKind.Dns || resolver.Zone is null)
        {
            throw new HostUnreachableException($"{resolverAddress} (DNS)");
        }

        return resolver.Zone;
    }
}
