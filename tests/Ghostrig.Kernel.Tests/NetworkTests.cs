using System.Text.RegularExpressions;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Events;
using Ghostrig.Kernel.Network;
using Ghostrig.Kernel.Tests.Support;
using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel.Tests;

/// <summary>Exercises the M1 exit criterion: DNS → connect → login → ACL denial → escalation (Act I).</summary>
public class NetworkTests
{
    [Fact]
    public void Domains_resolve_through_the_configured_dns_server()
    {
        var kernel = Fixtures.Act1Kernel();
        var resolved = new List<NetResolveEvent>();
        using var _ = kernel.Events.Subscribe<NetResolveEvent>(resolved.Add);

        var ip = kernel.Network.Resolve(kernel.LocalNode, "Verbena-and-Rust.NY56.");

        Assert.Equal(Fixtures.Verbena, ip);
        Assert.Equal("verbena-and-rust.ny56", Assert.Single(resolved).Hostname);
        Assert.Equal(["verbena-and-rust.ny56"], kernel.Network.ReverseLookup(kernel.LocalNode, ip));
    }

    [Theory]
    [InlineData("aegis-logistics.ny56")]
    [InlineData("vex_drop.lotus.ny56")]
    public void Unknown_or_malformed_names_return_nxdomain(string hostname)
    {
        var kernel = Fixtures.Act1Kernel();
        var ex = Assert.Throws<DnsResolutionException>(() => kernel.Network.Resolve(kernel.LocalNode, hostname));
        Assert.Equal("NXDOMAIN", ex.Code);
    }

    [Fact]
    public void A_dead_resolver_makes_dns_unreachable()
    {
        var kernel = Fixtures.Act1Kernel();
        kernel.Network.GetNode(Fixtures.Resolver).Online = false;
        Assert.Throws<HostUnreachableException>(() => kernel.Network.Resolve(kernel.LocalNode, "verbena-and-rust.ny56"));
    }

    [Fact]
    public void Closed_ports_refuse_and_unknown_hosts_are_unreachable()
    {
        var kernel = Fixtures.Act1Kernel();
        Assert.Equal("ECONNREFUSED", Assert.Throws<ConnectionRefusedException>(
            () => kernel.Network.Connect(kernel.LocalNode, Fixtures.Verbena, 8443)).Code);
        Assert.Throws<HostUnreachableException>(() => kernel.Network.Connect(kernel.LocalNode, IpAddress.Parse("10.99.0.1"), 80));
    }

    [Fact]
    public void Public_storefront_is_served_from_the_web_root_to_guests()
    {
        var kernel = Fixtures.Act1Kernel();
        using var http = kernel.Network.Connect(kernel.LocalNode, Fixtures.Verbena, 80, "verbena-and-rust.ny56").Anonymous();

        Assert.Contains("Verbena & Rust", http.Read("/index.cform").ReadText());
        Assert.Throws<NetworkProtocolException>(() => http.Write("/defaced.cform", FileContent.FromText("x")));
        // The web root is a chroot: there is no way up to the logs.
        Assert.Throws<VfsNotFoundException>(() => http.Read("/var/log/httpd/auth_audit.log"));
        Assert.Throws<InvalidVfsPathException>(() => http.Read("/../../var/log/httpd/auth_audit.log"));
    }

    [Fact]
    public void Ftp_rejects_anonymous_access_and_logs_failed_logins()
    {
        var kernel = Fixtures.Act1Kernel();
        var ftp = kernel.Network.Connect(kernel.LocalNode, Fixtures.Verbena, 2121);
        Assert.Throws<AuthenticationFailedException>(ftp.Anonymous);
        Assert.Equal("401", Assert.Throws<AuthenticationFailedException>(() => ftp.Login("v.chen", "wrong")).Code);

        var log = kernel.Network.GetNode(Fixtures.Verbena).FileSystem.ReadText("/var/log/httpd/auth_audit.log", Principal.System);
        Assert.Contains($"[v.chen] [{KernelBuilder.DefaultLocalAddress}] [LOGIN_FAIL]", log);
        Assert.Matches(new Regex(@"\[2056-11-17T23:42:\d\d\] \[anonymous\]"), log);
    }

    [Fact]
    public void Act_one_escalation_path_works_end_to_end()
    {
        var kernel = Fixtures.Act1Kernel();
        var denied = new List<FsAccessDeniedEvent>();
        using var _ = kernel.Events.Subscribe<FsAccessDeniedEvent>(denied.Add);

        // 1. DNS, then FTP as v.chen with the password recovered from Wren's receipt hash.
        var ip = kernel.Network.Resolve(kernel.LocalNode, "verbena-and-rust.ny56");
        using var chen = kernel.Network.Connect(kernel.LocalNode, ip, 2121, "verbena-and-rust.ny56").Login("v.chen", "orchidroots56");
        Assert.Contains(chen.List("/srv/ftp/storefront"), e => e.Name == "supplier_manifests");

        // 2. The hidden archive exists but is Root-only.
        Assert.Contains(chen.List("/", includeHidden: true), e => e.Name == ".root_archive");
        Assert.Equal("403", Assert.Throws<AccessDeniedException>(() => chen.List("/.root_archive")).Code);
        Assert.Equal("/.root_archive", Assert.Single(denied).Path);

        // 3. v.chen can read the auth audit log, which leaks root's weakly salted hash...
        var audit = chen.Read("/var/log/httpd/auth_audit.log").ReadText();
        var leaked = Regex.Match(audit, @"hash=(sha256\$\w*\$[0-9a-f]{64})").Groups[1].Value;
        Assert.True(PasswordHashing.Verify(leaked, "verbena1"), "the leaked hash should crack to the root password");
        Assert.Contains("[root] [10.56.14.2]", audit);

        // 4. ...so Kael escalates to Root and reads the manifest.
        using var root = kernel.Network.Connect(kernel.LocalNode, ip, 2121).Login("root", "verbena1");
        var manifest = root.Read("/.root_archive/manifest_tuesday_2342.txt").ReadText();
        Assert.Contains("BIO-ASSET-09 (UN-AUGMENTED / VIABLE)", manifest);
        Assert.Contains("Pier 44, Sub-Level B", manifest);
        Assert.Equal(Vfs.FileType.Encrypted, root.Stat("/.root_archive/aegis_shell_ledger.enc").Type);
    }

    [Fact]
    public void Uploads_publish_narrative_signatures_with_host_and_payload_hash()
    {
        var kernel = Fixtures.Act1Kernel();
        var uploads = new List<NetFtpUploadEvent>();
        using var _ = kernel.Events.Subscribe<NetFtpUploadEvent>(uploads.Add);

        using var chen = kernel.Network.Connect(kernel.LocalNode, Fixtures.Verbena, 2121, "verbena-and-rust.ny56").Login("v.chen", "orchidroots56");
        var payload = FileContent.FromText("aegis topology");
        chen.Write("/srv/ftp/storefront/drop.txt", payload);

        var upload = Assert.Single(uploads);
        Assert.Equal("Net.FtpUpload", upload.EventType);
        Assert.Equal("verbena-and-rust.ny56", upload.DestinationHost);
        Assert.Equal(payload.ContentHash, upload.PayloadHash);
        Assert.StartsWith("sha256:", upload.PayloadHash);
    }

    [Fact]
    public void Closed_sessions_reject_further_operations()
    {
        var kernel = Fixtures.Act1Kernel();
        var chen = kernel.Network.Connect(kernel.LocalNode, Fixtures.Verbena, 2121).Login("v.chen", "orchidroots56");
        chen.Dispose();
        Assert.Equal("ESHUTDOWN", Assert.Throws<SessionClosedException>(() => chen.List("/")).Code);
    }

    [Theory]
    [InlineData("10.56.14.88", true)]
    [InlineData("10.56.14", false)]
    [InlineData("10.56.14.256", false)]
    [InlineData("010.1.1.1", false)]
    public void Ip_addresses_parse_strictly(string text, bool valid)
    {
        Assert.Equal(valid, IpAddress.TryParse(text, out var ip));
        if (valid)
        {
            Assert.Equal(text, ip.ToString());
        }
    }
}
