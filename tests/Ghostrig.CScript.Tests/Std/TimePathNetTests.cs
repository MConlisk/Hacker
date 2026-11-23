using static Ghostrig.CScript.Tests.Std.StdProgram;

namespace Ghostrig.CScript.Tests.Std;

public class TimeTests
{
    private const string Imports = "import Std.Time;";

    [Fact]
    public void World_calendar_parsing_formatting_and_arithmetic()
    {
        var result = Run(Imports, """
            string Main() {
                var vanished = DateTime.Parse("2056-11-14T23:42:00");
                var epoch = DateTime.Epoch;
                var gap = epoch.Subtract(vanished);
                var later = DateTime.FromInstant(Time.Add(epoch.ToInstant(), Duration.FromHours(25)));
                return $"{epoch}|{epoch.DayOfWeek}|{vanished.DayOfWeek}|{gap.TotalSeconds / 3600.0}|{later}|{later.Format("ddd dd MMM yyyy HH:mm")}|{vanished.ToInstant().Nanoseconds < 0}|{vanished.AddMonths(3).Format("yyyy-MM")}|{DateTime.TryParse("garbage") == null}|{vanished.Date}";
            }
            """);
        Assert.Equal("2056-11-17T23:42:00|Friday|Tuesday|72.0|2056-11-19T00:42:00|Sun 19 Nov 2056 00:42|true|2057-02|true|2056-11-14T00:00:00", result);
    }

    [Fact]
    public void Durations_parse_and_format()
    {
        var result = Run(Imports, """
            string Main() => $"{Time.ParseDuration("1h30m").TotalSeconds}|{Time.ParseDuration("250ms").TotalMilliseconds}|{Time.ParseDuration("90").TotalSeconds}|{Time.FormatDuration(Duration.FromSeconds(3725))}|{Time.Between(Time.Add(DateTime.Epoch.ToInstant(), Duration.FromSeconds(10)), DateTime.Epoch.ToInstant())}";
            """);
        Assert.Equal("5400.0|250.0|90.0|1h 2m 5s|-10s", result);
    }

    [Fact]
    public void Stopwatch_measures_simulated_time()
    {
        var program = Load("""
            module Test;
            import Std.Time;
            async Task<string> Main() {
                var sw = Stopwatch.StartNew();
                await Task.Delay(Duration.FromSeconds(2));
                sw.Stop();
                await Task.Delay(Duration.FromSeconds(5));
                var stopped = sw.Elapsed;
                sw.Start();
                await Task.Delay(Duration.FromSeconds(1));
                return $"{stopped.TotalSeconds >= 2.0 && stopped.TotalSeconds < 2.01}|{sw.Elapsed.TotalSeconds >= 3.0 && sw.Elapsed.TotalSeconds < 3.01}|{sw.IsRunning}";
            }
            """);
        Assert.Equal("true|true|true", program.Call("Main").AsString);
    }

    [Fact]
    public void Cron_schedules_find_the_next_run()
    {
        var result = Run(Imports, """
            string Main() {
                var nightly = Cron.Parse("30 2 * * *");
                var weekdays = Cron.Parse("*/15 9-17 * * MON-FRI");
                var from = DateTime.Parse("2056-11-17T23:42:00");   // Friday night
                var runs = weekdays.NextN(from, 2);
                return $"{nightly.Next(from)}|{runs[0]}|{runs[1]}|{weekdays.Matches(DateTime.Parse("2056-11-20T09:45:00"))}|{Cron.Parse("0 0 1 JAN *").Next(from)}";
            }
            """);
        Assert.Equal("2056-11-18T02:30:00|2056-11-20T09:00:00|2056-11-20T09:15:00|true|2057-01-01T00:00:00", result);
        Assert.Contains("FormatError", ErrorOf(Imports, """string Main() => Cron.Parse("61 * * * *").ToString();"""));
    }
}

public class PathTests
{
    [Fact]
    public void Paths_follow_the_vfs_rules()
    {
        var result = Run("import Std.Path;", """
            string Main() => $"{Path.Normalize("/var//log/./ftp/../http.log")}|{Path.Join("/home/kael", "notes.txt")}|{Path.Join("/home/kael", "/etc/passwd")}|{Path.Join(["/", "sys", "drivers", "net"])}|{Path.Parent("/sys/drivers/NetCardDriver.drv")}|{Path.Name("/sys/drivers/NetCardDriver.drv")}|{Path.Extension("/data/catalog_orchids.CSV")}|{Path.Stem("/data/catalog_orchids.csv")}|{Path.ChangeExtension("/a/b.txt", "md")}|{Path.IsHidden("/home/kael/.root_archive/x")}|{Path.Relative("/home/kael/docs", "/home/kael/logs/ftp.log")}|{Path.IsAncestor("/sys", "/sys/drivers")}|{Path.Glob("/sys/drivers/**/*.drv", "/sys/drivers/gpu/LegacyGPUDriver.drv")}|{Path.IsValid("relative/path")}|{Path.Parent("/")}";
            """);
        Assert.Equal("/var/log/http.log|/home/kael/notes.txt|/etc/passwd|/sys/drivers/net|/sys/drivers|NetCardDriver.drv|.csv|catalog_orchids|/a/b.md|true|../logs/ftp.log|true|true|false|/", result);
        Assert.Contains("InvalidPath", ErrorOf("import Std.Path;", """string Main() => Path.Normalize("/../etc");"""));
    }
}

public class NetTests
{
    private const string Imports = "import Std.Net;\nimport Std.Seq;\nimport Std.Collections;\nimport Std.Binary;";

    [Fact]
    public void Addresses_subnets_and_ports()
    {
        var result = Run(Imports, """
            string Main() {
                var ip = IpAddress.Parse("10.56.4.20");
                var subnet = Cidr.Parse("10.56.4.17/28");
                var hosts = subnet.Hosts().ToArray();
                var seen = new Set<IpAddress>(); seen.Add(IpAddress.Parse("10.0.0.1")); seen.Add(IpAddress.FromInt(167772161));
                return $"{ip.ToInt()}|{ip.IsPrivate}|{subnet}|{subnet.Mask}|{subnet.BroadcastAddress}|{subnet.Size}|{hosts.Length}|{hosts[0]}|{subnet.Contains(ip)}|{subnet.Contains(IpAddress.Parse("10.56.4.31"))}|{Cidr.Parse("10.56.0.0/16").Subnets(18).Length}|{ip.Add(300)}|{seen.Count}|{Port.ServiceName(2121)}|{Port.ForService("mgmt-tls")}|{IpAddress.TryParse("256.1.1.1") == null}|{ip == IpAddress.Parse("10.56.4.20")}";
            }
            """);
        Assert.Equal("171443220|true|10.56.4.16/28|255.255.255.240|10.56.4.31|16|14|10.56.4.17|true|true|4|10.56.5.64|1|ftp-alt|8443|true|true", result);
    }

    [Fact]
    public void Urls_hostnames_mac_and_headers()
    {
        var result = Run(Imports, """
            string Main() {
                var url = Url.Parse("https://admin@Verbena-And-Rust.ny56:8443/portal/login?user=kael&next=%2Forders#top");
                var moved = url.WithPath("/api").WithParam("user", "wren");
                var mac = MacAddress.Parse("00-1A-2B-3C-4D-5E");
                var headers = HttpHeaders.Parse("Content-Type: text/csv\r\nX-Relay: dark-07\r\n").With("content-type", "application/json");
                return $"{url.Host}|{url.Port}|{url.User}|{url.Param("next")}|{url.Params.Count}|{moved}|{Url.Parse("http://x.ny56").Port}|{Hostname.IsValid("verbena-and-rust.ny56")}|{Hostname.IsValid("-bad.ny56")}|{mac}|{mac.Oui}|{headers.Get("CONTENT-TYPE")}|{headers.Names.Length}";
            }
            """);
        Assert.Equal("verbena-and-rust.ny56|8443|admin|/orders|2|https://admin@verbena-and-rust.ny56:8443/api?next=%2Forders&user=wren#top|80|true|false|00:1a:2b:3c:4d:5e|00:1a:2b|application/json|2", result);
    }

    [Fact]
    public void Packets_carry_valid_checksums_and_parse_back()
    {
        var result = Run(Imports, """
            string Main() {
                var src = IpAddress.Parse("10.56.0.2"); var dst = IpAddress.Parse("10.56.4.20");
                var syn = Packet.TcpSegment(src, dst, 40000, 2121, 1, 0, Packet.Syn, bytes.Empty);
                var packet = Packet.Ipv4(src, dst, Packet.Tcp, 64, syn);
                var parsed = Packet.ParseIpv4(packet);
                var tcp = new BinaryReader(parsed.Payload, Endian.Big);
                var srcPort = tcp.ReadU16(); var dstPort = tcp.ReadU16();
                var ping = Packet.IcmpMessage(8, 0, 1, 1, bytes.FromText("hello"));
                return $"{packet.Length}|{parsed.Source}|{parsed.Destination}|{parsed.Protocol}|{parsed.Ttl}|{parsed.ChecksumValid}|{srcPort}->{dstPort}|{Packet.FlagNames(Packet.Syn | Packet.Ack)}|{Checksum.Internet(ping)}|{Packet.UdpDatagram(src, dst, 53, 53, bytes.FromText("q")).Length}";
            }
            """);
        Assert.Equal("40|10.56.0.2|10.56.4.20|6|64|true|40000->2121|SYN|ACK|0|9", result);
    }
}
