using System.Globalization;

namespace Ghostrig.Kernel.Network;

/// <summary>Simulated IPv4 address.</summary>
public readonly record struct IpAddress(uint Value) : IComparable<IpAddress>
{
    public static IpAddress Parse(string text)
    {
        if (!TryParse(text, out var address))
        {
            throw new FormatException($"'{text}' is not a valid IPv4 address.");
        }

        return address;
    }

    public static bool TryParse(string? text, out IpAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        uint value = 0;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0')
                || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet))
            {
                return false;
            }

            value = (value << 8) | octet;
        }

        address = new IpAddress(value);
        return true;
    }

    public int CompareTo(IpAddress other) => Value.CompareTo(other.Value);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Value >> 24}.{(Value >> 16) & 0xFF}.{(Value >> 8) & 0xFF}.{Value & 0xFF}");
}
