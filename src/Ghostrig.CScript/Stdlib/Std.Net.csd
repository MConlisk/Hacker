/// Network value types and packet layouts (Document 7 §13.9). Pure: nothing here touches the network — the Net system
/// library (M3) performs I/O with these types. Addresses are IPv4, as on every NY-56 network.
module Std.Net;

import Std.Collections;
import Std.Seq;

class IpAddress : Comparable<IpAddress>, Equatable<IpAddress>, Hashable {
    /// Dotted quad; raises FormatError.
    static IpAddress Parse(string text);
    static IpAddress? TryParse(string text);
    static IpAddress FromInt(int value);
    static IpAddress Any { get; }
    static IpAddress Loopback { get; }
    static IpAddress Broadcast { get; }
    int ToInt();
    int[] Octets { get; }
    bool IsPrivate { get; }
    bool IsLoopback { get; }
    bool IsMulticast { get; }
    /// The address offset by n (wrapping within 32 bits).
    IpAddress Add(int n);
    int CompareTo(IpAddress other);
    bool Equals(IpAddress other);
    int HashCode();
    string ToString();
}

/// An IPv4 subnet in CIDR notation.
class Cidr : Equatable<Cidr>, Hashable {
    /// "10.56.0.0/16"; host bits are cleared.
    static Cidr Parse(string text);
    IpAddress Network { get; }
    int PrefixLength { get; }
    IpAddress Mask { get; }
    IpAddress BroadcastAddress { get; }
    /// Number of addresses (2^(32 − prefix)).
    int Size { get; }
    bool Contains(IpAddress address);
    bool Overlaps(Cidr other);
    /// Usable host addresses (excludes network and broadcast for prefixes below /31), lazily.
    Sequence<IpAddress> Hosts();
    /// Splits into subnets with a longer prefix.
    Cidr[] Subnets(int newPrefixLength);
    bool Equals(Cidr other);
    int HashCode();
    string ToString();
}

class Port {
    static bool IsValid(int port);
    /// Conventional service name ("ftp", "http", "mgmt-tls"…), or null.
    static string? ServiceName(int port);
    /// The conventional port of a service, or -1.
    static int ForService(string service);
    static bool IsPrivileged(int port);
}

class Hostname {
    /// RFC 1123 hostnames (letters, digits, hyphens; labels of 1–63 characters; at most 253 total).
    static bool IsValid(string name);
    static string[] Labels(string name);
    /// "verbena-and-rust.ny56" → "ny56".
    static string TopLevel(string name);
}

class MacAddress : Equatable<MacAddress>, Hashable {
    /// "00:1a:2b:3c:4d:5e" or "00-1A-2B-3C-4D-5E".
    static MacAddress Parse(string text);
    bytes Bytes { get; }
    /// The vendor prefix ("00:1a:2b").
    string Oui { get; }
    bool IsBroadcast { get; }
    bool IsMulticast { get; }
    bool Equals(MacAddress other);
    int HashCode();
    string ToString();
}

/// An absolute URL (scheme://[user@]host[:port]/path?query#fragment).
class Url : Equatable<Url> {
    static Url Parse(string text);
    static Url? TryParse(string text);
    string Scheme { get; }
    string User { get; }
    string Host { get; }
    /// Explicit port, or the scheme's default (http 80, https 443, ftp 21), or -1.
    int Port { get; }
    string Path { get; }
    /// Raw query without '?'.
    string Query { get; }
    string Fragment { get; }
    string? Param(string name);
    Map<string, string> Params { get; }
    Url WithPath(string path);
    Url WithParam(string name, string value);
    Url WithHost(string host);
    bool Equals(Url other);
    string ToString();
}

/// Case-insensitive HTTP header fields, in order.
class HttpHeaders {
    static HttpHeaders Empty { get; }
    /// "Name: value" lines.
    static HttpHeaders Parse(string text);
    string? Get(string name);
    bool Has(string name);
    string[] Names { get; }
    HttpHeaders With(string name, string value);
    HttpHeaders Without(string name);
    string ToString();
}

/// Decoded IPv4 header.
class Ipv4Packet {
    IpAddress Source { get; }
    IpAddress Destination { get; }
    int Protocol { get; }
    int Ttl { get; }
    int Identification { get; }
    bytes Payload { get; }
    bool ChecksumValid { get; }
    string ToString();
}

/// Raw packet construction and parsing (checksums are computed for you).
class Packet {
    const int Icmp = 1;
    const int Tcp = 6;
    const int Udp = 17;
    const int Fin = 1;
    const int Syn = 2;
    const int Rst = 4;
    const int Psh = 8;
    const int Ack = 16;
    const int Urg = 32;
    static bytes Ipv4(IpAddress source, IpAddress destination, int protocol, int ttl, bytes payload);
    /// TCP segment with the pseudo-header checksum for the given addresses.
    static bytes TcpSegment(IpAddress source, IpAddress destination, int sourcePort, int destinationPort, int sequence, int acknowledgement, int flags, bytes payload);
    static bytes UdpDatagram(IpAddress source, IpAddress destination, int sourcePort, int destinationPort, bytes payload);
    /// ICMP echo request (type 8) or reply (type 0) and other simple messages.
    static bytes IcmpMessage(int type, int code, int identifier, int sequence, bytes payload);
    static Ipv4Packet ParseIpv4(bytes data);
    /// Names of the TCP flags set, e.g. "SYN|ACK".
    static string FlagNames(int flags);
}
