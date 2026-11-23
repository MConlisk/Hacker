using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Events;
using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel.Network;

/// <summary>An open transport to one service on one node, prior to authentication.</summary>
public sealed class Connection
{
    private readonly NetworkSimulator _network;

    internal Connection(NetworkSimulator network, NetworkNode client, NetworkNode server, ServiceBinding service, string? hostname)
    {
        _network = network;
        Client = client;
        Server = server;
        Service = service;
        Hostname = hostname;
    }

    public NetworkNode Client { get; }
    public NetworkNode Server { get; }
    public ServiceBinding Service { get; }

    /// <summary>The DNS name the client used to reach the server, if any.</summary>
    public string? Hostname { get; }

    /// <summary>Authenticates with an account on the server. Every attempt is written to the access log.</summary>
    public Session Login(string userName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(secret);
        RequireInteractive();

        var ok = Server.Accounts.TryGetValue(userName, out var account) && account.Verify(secret);
        _network.Log(Server, Service, userName, Client.Address, ok ? "LOGIN_OK" : "LOGIN_FAIL");
        _network.Events.Publish(new NetLoginEvent(Client.Address.ToString(), Server.Address.ToString(), Service.Port, userName, ok));
        if (!ok)
        {
            throw new AuthenticationFailedException($"{Server.Address}:{Service.Port}", userName);
        }

        return new Session(_network, this, account!.Principal);
    }

    /// <summary>Opens a guest session where the service permits anonymous access (public web pages, open FTP).</summary>
    public Session Anonymous()
    {
        if (Service.Kind == ServiceKind.Dns)
        {
            throw new NetworkProtocolException("DNS does not offer sessions.");
        }

        if (!Service.AllowAnonymous)
        {
            _network.Log(Server, Service, Principal.Anonymous.UserName, Client.Address, "ANON_DENIED");
            throw new AuthenticationFailedException($"{Server.Address}:{Service.Port}", Principal.Anonymous.UserName);
        }

        _network.Log(Server, Service, Principal.Anonymous.UserName, Client.Address, "ANON_OK");
        return new Session(_network, this, Principal.Anonymous);
    }

    private void RequireInteractive()
    {
        if (Service.Kind is ServiceKind.Dns)
        {
            throw new NetworkProtocolException("DNS does not accept logins.");
        }
    }
}

/// <summary>
/// An authenticated session. Paths are relative to the service root (web root / FTP chroot) and every access is
/// validated against the remote VFS ACLs under the session's principal.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly NetworkSimulator _network;
    private bool _closed;

    internal Session(NetworkSimulator network, Connection connection, Principal principal)
    {
        _network = network;
        Connection = connection;
        Principal = principal;
    }

    public Connection Connection { get; }
    public Principal Principal { get; }

    private VirtualFileSystem Fs => Connection.Server.FileSystem;

    public VfsEntryInfo Stat(VfsPath path) => Guard(path, "stat", p => Fs.Stat(p, Principal));

    public IReadOnlyList<VfsEntryInfo> List(VfsPath path, bool includeHidden = false) =>
        Guard(path, "list", p => Fs.List(p, Principal, includeHidden));

    /// <summary>Downloads a file (FTP/management) or fetches a page or asset (HTTP).</summary>
    public FileContent Read(VfsPath path)
    {
        var content = Guard(path, "read", p => Fs.Read(p, Principal));
        if (Connection.Service.CarriesFiles)
        {
            _network.Events.Publish(new NetFtpDownloadEvent(Connection.Client.Address.ToString(), Connection.Server.Address.ToString(),
                Connection.Service.Port, Principal.UserName, path.Value, content.ContentHash));
        }

        return content;
    }

    /// <summary>Uploads a file. Only file-carrying services (FTP, management channel) accept writes.</summary>
    public VfsEntryInfo Write(VfsPath path, FileContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Connection.Service.CarriesFiles)
        {
            throw new NetworkProtocolException($"{Connection.Service.Kind} on port {Connection.Service.Port} does not accept uploads.");
        }

        var info = Guard(path, "write", p => Fs.Write(p, Principal, content));
        _network.Log(Connection.Server, Connection.Service, Principal.UserName, Connection.Client.Address, $"STOR {path}");
        _network.Events.Publish(new NetFtpUploadEvent(Connection.Client.Address.ToString(), Connection.Server.Address.ToString(),
            Connection.Hostname, Connection.Service.Port, Principal.UserName, path.Value, content.ContentHash));
        return info;
    }

    public void Dispose() => _closed = true;

    private T Guard<T>(VfsPath sessionPath, string operation, Func<VfsPath, T> action)
    {
        if (_closed)
        {
            throw new SessionClosedException();
        }

        var mapped = sessionPath.Under(Connection.Service.Root);
        try
        {
            return action(mapped);
        }
        catch (AccessDeniedException)
        {
            _network.Events.Publish(new FsAccessDeniedEvent(Connection.Server.Address.ToString(), mapped.Value, Principal.UserName, operation));
            throw;
        }
    }
}
