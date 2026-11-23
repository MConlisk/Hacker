using System.Security.Cryptography;
using System.Text;
using Ghostrig.Kernel.Determinism;

namespace Ghostrig.Kernel.Vfs;

/// <summary>Byte alphabet for procedurally generated file bodies.</summary>
public enum SyntheticAlphabet
{
    /// <summary>Uniform bytes 0–255 (ciphertext, media payloads).</summary>
    Binary,

    /// <summary>Printable ASCII lines of 31 characters plus newline (wordlists, rainbow tables).</summary>
    PrintableLines,
}

/// <summary>
/// Immutable file body. Small files are stored inline; very large files (a 4 GB rainbow table) are
/// <see cref="SyntheticFileContent"/>: deterministic, random-access and never materialized in host memory.
/// </summary>
public abstract class FileContent
{
    private string? _hash;

    public abstract long Length { get; }

    /// <summary>Stable content identifier used by the narrative vault (authentic vs. doctored files).</summary>
    public string ContentHash => _hash ??= ComputeHash();

    /// <summary>Copies up to <paramref name="destination"/>.Length bytes starting at <paramref name="offset"/>.</summary>
    public abstract int ReadAt(long offset, Span<byte> destination);

    public byte[] ReadAll(long maxBytes = 64L * 1024 * 1024)
    {
        if (Length > maxBytes)
        {
            throw new InvalidOperationException(
                $"Refusing to materialize {Length:N0} bytes (limit {maxBytes:N0}); read the file in ranges instead.");
        }

        var buffer = new byte[Length];
        ReadAt(0, buffer);
        return buffer;
    }

    public string ReadText() => Encoding.UTF8.GetString(ReadAll());

    protected abstract string ComputeHash();

    public static InlineFileContent FromText(string text) => new(Encoding.UTF8.GetBytes(text));

    public static InlineFileContent FromBytes(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());

    public static readonly InlineFileContent Empty = new([]);
}

/// <summary>File body held in memory. The backing array is private and never mutated.</summary>
public sealed class InlineFileContent : FileContent
{
    private readonly byte[] _bytes;

    internal InlineFileContent(byte[] bytes) => _bytes = bytes;

    public override long Length => _bytes.LongLength;

    public ReadOnlySpan<byte> Span => _bytes;

    public override int ReadAt(long offset, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= _bytes.LongLength)
        {
            return 0;
        }

        var n = (int)Math.Min(destination.Length, _bytes.LongLength - offset);
        _bytes.AsSpan((int)offset, n).CopyTo(destination);
        return n;
    }

    /// <summary>Returns a new content with <paramref name="suffix"/> appended.</summary>
    public InlineFileContent Append(ReadOnlySpan<byte> suffix)
    {
        var combined = new byte[_bytes.Length + suffix.Length];
        _bytes.CopyTo(combined, 0);
        suffix.CopyTo(combined.AsSpan(_bytes.Length));
        return new InlineFileContent(combined);
    }

    protected override string ComputeHash() => "sha256:" + Convert.ToHexString(SHA256.HashData(_bytes)).ToLowerInvariant();
}

/// <summary>Deterministic procedurally generated body addressed by (length, seed, alphabet).</summary>
public sealed class SyntheticFileContent : FileContent
{
    public SyntheticFileContent(long length, ulong seed, SyntheticAlphabet alphabet)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        Seed = seed;
        Alphabet = alphabet;
    }

    public override long Length { get; }
    public ulong Seed { get; }
    public SyntheticAlphabet Alphabet { get; }

    public override int ReadAt(long offset, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length)
        {
            return 0;
        }

        var n = (int)Math.Min(destination.Length, Length - offset);
        for (var i = 0; i < n; i++)
        {
            destination[i] = ByteAt(offset + i);
        }

        return n;
    }

    private byte ByteAt(long index)
    {
        if (Alphabet == SyntheticAlphabet.PrintableLines && index % 32 == 31)
        {
            return (byte)'\n';
        }

        var state = Seed ^ ((ulong)(index >> 3) * 0xD1B54A32D192ED03UL);
        var word = DeterministicRandom.SplitMix64(ref state);
        var b = (byte)(word >> (int)((index & 7) * 8));
        return Alphabet == SyntheticAlphabet.Binary ? b : (byte)('!' + (b % 94));
    }

    protected override string ComputeHash() =>
        "synthetic:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Length}|{Seed}|{Alphabet}"))).ToLowerInvariant();
}
