using System.Collections.Immutable;
using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel.Network;

/// <summary>Listening daemon types (Doc 3 §5.2).</summary>
public enum ServiceKind
{
    /// <summary>HTTP / CForms web server (read-only, serves the web root).</summary>
    Http,

    /// <summary>FTP daemon (Port 2121 by convention).</summary>
    Ftp,

    /// <summary>Management gateway (Port 8443) exposing an authenticated file channel.</summary>
    Management,

    /// <summary>DNS resolver (Port 53) serving a zone.</summary>
    Dns,
}

/// <summary>A daemon bound to a port on a node.</summary>
public sealed record ServiceBinding
{
    public ServiceBinding(int port, ServiceKind kind, VfsPath root, VfsPath? accessLog = null, bool allowAnonymous = false,
        string? banner = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Port = port;
        Kind = kind;
        Root = root;
        AccessLog = accessLog;
        AllowAnonymous = allowAnonymous;
        Banner = banner ?? kind switch
        {
            ServiceKind.Http => "httpd/cforms",
            ServiceKind.Ftp => "ftpd",
            ServiceKind.Management => "mgmt-gateway",
            _ => "named",
        };
    }

    public int Port { get; }
    public ServiceKind Kind { get; }

    /// <summary>Directory the service exposes as "/" (web root for HTTP, chroot for FTP / management).</summary>
    public VfsPath Root { get; }

    /// <summary>Log file receiving structured <c>[Timestamp] [User] [Source_IP] [Action]</c> entries.</summary>
    public VfsPath? AccessLog { get; }

    public bool AllowAnonymous { get; }
    public string Banner { get; }

    public bool CarriesFiles => Kind is ServiceKind.Ftp or ServiceKind.Management;
}

/// <summary>Forward/reverse DNS records served by a node's DNS daemon.</summary>
public sealed class DnsZone
{
    private readonly ImmutableSortedDictionary<string, IpAddress> _forward;

    public DnsZone(IEnumerable<KeyValuePair<string, IpAddress>> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, IpAddress>(StringComparer.Ordinal);
        foreach (var (name, address) in records)
        {
            var normalized = Normalize(name);
            if (!builder.TryAdd(normalized, address))
            {
                throw new ArgumentException($"Duplicate DNS record '{normalized}'.", nameof(records));
            }
        }

        _forward = builder.ToImmutable();
    }

    public IReadOnlyDictionary<string, IpAddress> Records => _forward;

    public bool TryResolve(string hostname, out IpAddress address) => _forward.TryGetValue(Normalize(hostname), out address);

    public IReadOnlyList<string> ReverseLookup(IpAddress address) =>
        _forward.Where(r => r.Value == address).Select(r => r.Key).ToList();

    /// <summary>Lower-cases and validates a hostname (letters, digits, hyphens and dots; no underscores).</summary>
    public static string Normalize(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        var name = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')
                || !label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                throw new ArgumentException($"'{hostname}' is not a valid hostname.", nameof(hostname));
            }
        }

        return name;
    }
}
