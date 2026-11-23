using System.Collections.Immutable;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Json;
using Ghostrig.Kernel.Network;
using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel.World;

/// <summary>A file to seed into a host's VFS.</summary>
public sealed record FileDefinition(VfsPath Path, FileContent Content, Acl? Acl, bool Signed);

/// <summary>A directory to create (with parents) and optionally protect with an explicit ACL.</summary>
public sealed record DirectoryDefinition(VfsPath Path, Acl? Acl);

/// <summary>Declarative definition of one network node (Doc 5 §3).</summary>
public sealed record HostDefinition(
    IpAddress Address,
    string Label,
    bool Online,
    IpAddress? Resolver,
    Acl RootAcl,
    ImmutableArray<Account> Accounts,
    ImmutableArray<ServiceBinding> Services,
    ImmutableArray<KeyValuePair<string, IpAddress>> DnsRecords,
    ImmutableArray<DirectoryDefinition> Directories,
    ImmutableArray<FileDefinition> Files);

/// <summary>
/// A validated world content file (<c>content/world/*.json</c>): the hosts, DNS zones and filesystems that make up
/// part of the in-game internet. Content is data (Roadmap principle 4).
/// </summary>
public sealed class WorldManifest
{
    public const int SupportedSchemaVersion = 1;

    public WorldManifest(string source, ImmutableArray<HostDefinition> hosts)
    {
        Source = source;
        Hosts = hosts;
    }

    public string Source { get; }
    public ImmutableArray<HostDefinition> Hosts { get; }

    public static WorldManifest Load(string path) => Parse(File.ReadAllText(path), path);

    public static WorldManifest Parse(string json, string source = "world.json")
    {
        using var document = JsonFields.Parse(json, source);
        var root = new JsonFields(document.RootElement, source, "$");
        var version = root.RequireInt32("schema_version");
        if (version != SupportedSchemaVersion)
        {
            throw root.Error($"unsupported schema_version {version}; expected {SupportedSchemaVersion}.");
        }

        var hosts = root.RequireObjectArray("hosts").Select(ParseHost).ToImmutableArray();
        var duplicate = hosts.GroupBy(h => h.Address).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw root.Error($"address {duplicate.Key} is defined more than once.");
        }

        return new WorldManifest(source, hosts);
    }

    /// <summary>Creates every host in the manifest on the simulator and seeds its filesystem.</summary>
    public void ApplyTo(NetworkSimulator network)
    {
        ArgumentNullException.ThrowIfNull(network);
        foreach (var host in Hosts)
        {
            var node = network.CreateNode(host.Address, host.Label, host.RootAcl);
            node.Online = host.Online;
            node.Resolver = host.Resolver;
            foreach (var account in host.Accounts)
            {
                node.AddAccount(account);
            }

            foreach (var service in host.Services)
            {
                node.Bind(service);
            }

            if (!host.DnsRecords.IsEmpty)
            {
                node.Zone = new DnsZone(host.DnsRecords);
            }

            var fs = node.FileSystem;
            foreach (var directory in host.Directories)
            {
                if (!fs.Exists(directory.Path))
                {
                    fs.CreateDirectory(directory.Path, Principal.System, directory.Acl, createParents: true);
                }
                else if (directory.Acl is not null)
                {
                    fs.SetAcl(directory.Path, Principal.System, directory.Acl);
                }
            }

            foreach (var file in host.Files)
            {
                if (!fs.Exists(file.Path.Parent))
                {
                    fs.CreateDirectory(file.Path.Parent, Principal.System, createParents: true);
                }

                fs.Write(file.Path, Principal.System, file.Content, file.Acl);
                if (file.Signed)
                {
                    fs.SignBaseline(file.Path, Principal.System);
                }
            }
        }
    }

    private static HostDefinition ParseHost(JsonFields h)
    {
        var address = ParseAddress(h, "address");
        var resolver = h.Has("resolver") ? ParseAddress(h, "resolver") : (IpAddress?)null;

        var accounts = h.OptionalObjectArray("accounts").Select(a => Guard(a, () =>
            new Account(a.RequireString("user"), a.RequireString("password_hash"), ParseTier(a, "tier")))).ToImmutableArray();

        var services = h.OptionalObjectArray("services").Select(s => Guard(s, () => new ServiceBinding(
            s.RequireInt32("port", 1, 65535),
            ParseServiceKind(s),
            s.Has("root") ? ParsePath(s, "root") : VfsPath.Root,
            s.Has("access_log") ? ParsePath(s, "access_log") : (VfsPath?)null,
            s.OptionalBool("anonymous", false),
            s.OptionalString("banner")))).ToImmutableArray();

        var records = h.OptionalObjectArray("dns_records")
            .Select(r => Guard(r, () => new KeyValuePair<string, IpAddress>(DnsZone.Normalize(r.RequireString("name")), ParseAddress(r, "address"))))
            .ToImmutableArray();
        if (!records.IsEmpty && !services.Any(s => s.Kind == ServiceKind.Dns))
        {
            throw h.Error("dns_records require a dns service binding.");
        }

        var directories = h.OptionalObjectArray("directories")
            .Select(d => new DirectoryDefinition(ParsePath(d, "path"), d.Has("acl") ? ParseAcl(d.RequireObject("acl")) : null))
            .ToImmutableArray();

        var files = h.OptionalObjectArray("files").Select(ParseFile).ToImmutableArray();

        return new HostDefinition(
            address,
            h.RequireString("label"),
            h.OptionalBool("online", true),
            resolver,
            h.Has("root_acl") ? ParseAcl(h.RequireObject("root_acl")) : VirtualFileSystem.DefaultRootAcl,
            accounts,
            services,
            records,
            directories,
            files);
    }

    private static FileDefinition ParseFile(JsonFields f)
    {
        var path = ParsePath(f, "path");
        if (!FileTypes.TryFromPath(path, out _))
        {
            throw f.Error($"'{path}' has an unsupported file type; allowed extensions: {string.Join(", ", FileTypes.SupportedExtensions)}.");
        }

        var sources = new[] { "text", "base64", "synthetic" }.Count(f.Has);
        if (sources != 1)
        {
            throw f.Error("exactly one of 'text', 'base64' or 'synthetic' must be provided.");
        }

        FileContent content;
        if (f.Has("text"))
        {
            content = FileContent.FromText(f.OptionalString("text") ?? string.Empty);
        }
        else if (f.Has("base64"))
        {
            try
            {
                content = FileContent.FromBytes(Convert.FromBase64String(f.RequireString("base64")));
            }
            catch (FormatException)
            {
                throw f.Error("'base64' is not valid Base64.");
            }
        }
        else
        {
            var s = f.RequireObject("synthetic");
            var alphabet = s.RequireString("alphabet") switch
            {
                "binary" => SyntheticAlphabet.Binary,
                "printable_lines" => SyntheticAlphabet.PrintableLines,
                var other => throw s.Error($"unknown alphabet '{other}'."),
            };
            content = new SyntheticFileContent(s.RequireInt64("length", 0), s.RequireUInt64("seed"), alphabet);
        }

        var signed = f.OptionalBool("stock_signed", false);
        return new FileDefinition(path, content, f.Has("acl") ? ParseAcl(f.RequireObject("acl")) : null, signed);
    }

    internal static Acl ParseAcl(JsonFields a)
    {
        var entries = a.OptionalObjectArray("entries").Select(e =>
        {
            AclSubject subject = (e.Has("tier"), e.Has("user")) switch
            {
                (true, false) => new AclSubject.TierAtLeast(ParseTier(e, "tier")),
                (false, true) => new AclSubject.User(e.RequireString("user")),
                _ => throw e.Error("an ACL entry needs exactly one of 'tier' or 'user'."),
            };
            return new AclEntry(subject, ParsePermissions(e));
        }).ToImmutableArray();

        var owner = a.OptionalString("owner") ?? "root";
        return new Acl(owner, a.OptionalString("group") ?? owner, entries);
    }

    private static Permissions ParsePermissions(JsonFields e)
    {
        var text = e.RequireString("perms");
        var p = Permissions.None;
        foreach (var ch in text)
        {
            p |= ch switch
            {
                'r' => Permissions.Read,
                'w' => Permissions.Write,
                'x' => Permissions.Execute,
                _ => throw e.Error($"invalid permission character '{ch}' (use r, w, x)."),
            };
        }

        return p;
    }

    internal static PrivilegeTier ParseTier(JsonFields f, string name) => f.RequireString(name) switch
    {
        "guest" => PrivilegeTier.Guest,
        "user" => PrivilegeTier.User,
        "operator" => PrivilegeTier.Operator,
        "root" => PrivilegeTier.Root,
        var other => throw f.Error($"unknown privilege tier '{other}'."),
    };

    private static ServiceKind ParseServiceKind(JsonFields s) => s.RequireString("kind") switch
    {
        "http" => ServiceKind.Http,
        "ftp" => ServiceKind.Ftp,
        "management" => ServiceKind.Management,
        "dns" => ServiceKind.Dns,
        var other => throw s.Error($"unknown service kind '{other}'."),
    };

    private static IpAddress ParseAddress(JsonFields f, string name) =>
        IpAddress.TryParse(f.RequireString(name), out var a) ? a : throw f.Error($"'{name}' is not a valid IPv4 address.");

    private static VfsPath ParsePath(JsonFields f, string name)
    {
        try
        {
            return VfsPath.Parse(f.RequireString(name));
        }
        catch (InvalidVfsPathException ex)
        {
            throw f.Error(ex.Message);
        }
    }

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
