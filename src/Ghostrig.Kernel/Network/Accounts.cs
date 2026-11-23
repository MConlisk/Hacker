using System.Security.Cryptography;
using System.Text;
using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel.Network;

/// <summary>
/// Credential hashes in the form <c>sha256$&lt;salt&gt;$&lt;hex&gt;</c> where hex = SHA-256(salt + secret).
/// An empty salt models the unsalted hashes Wren scribbled on her receipt; a short salt models the
/// "weakly salted" root hash leaked in <c>auth_audit.log</c>.
/// </summary>
public static class PasswordHashing
{
    private const string Scheme = "sha256";

    public static string Hash(string secret, string salt = "")
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Contains('$'))
        {
            throw new ArgumentException("Salt may not contain '$'.", nameof(salt));
        }

        return $"{Scheme}${salt}${Digest(salt + secret)}";
    }

    public static bool IsWellFormed(string stored)
    {
        var parts = stored.Split('$');
        return parts.Length == 3 && parts[0] == Scheme && parts[2].Length == 64 && parts[2].All(Uri.IsHexDigit);
    }

    public static bool Verify(string stored, string secret)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(secret);
        if (!IsWellFormed(stored))
        {
            return false;
        }

        var parts = stored.Split('$');
        var expected = Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant());
        var actual = Encoding.ASCII.GetBytes(Digest(parts[1] + secret));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>An account on a network node.</summary>
public sealed record Account
{
    public Account(string userName, string passwordHash, PrivilegeTier tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        if (!PasswordHashing.IsWellFormed(passwordHash))
        {
            throw new ArgumentException($"Malformed password hash for '{userName}'.", nameof(passwordHash));
        }

        UserName = userName;
        PasswordHash = passwordHash;
        Tier = tier;
    }

    public string UserName { get; }
    public string PasswordHash { get; }
    public PrivilegeTier Tier { get; }

    public Principal Principal => new(UserName, Tier);

    public bool Verify(string secret) => PasswordHashing.Verify(PasswordHash, secret);
}
