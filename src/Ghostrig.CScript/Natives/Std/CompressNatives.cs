using System.IO.Compression;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

/// <summary>Host implementations of <c>Std.Compress</c>. Every encoder is written here, so output is identical everywhere.</summary>
internal static class CompressNatives
{
    private const int MaxOutput = 64 << 20;

    public static void Register(NativeRegistry r)
    {
        static byte[] In(NativeContext c, Value v, int divisor)
        {
            var data = v.As<BytesObject>().Data;
            c.Charge(1 + (data.Length / divisor));
            return data;
        }

        static Value Out(NativeContext c, byte[] data, int divisor = 16)
        {
            c.Charge(data.Length / divisor);
            return Value.FromObject(new BytesObject(data));
        }

        var rle = r.Scope("Std.Compress.Rle");
        rle("Encode/1", (c, a) => Out(c, RleEncode(In(c, a[0], 4))));
        rle("Decode/1", (c, a) => Out(c, RleDecode(c, In(c, a[0], 16))));
        var lz = r.Scope("Std.Compress.Lz77");
        lz("Compress/1", (c, a) => Out(c, Lz77Compress(In(c, a[0], 4))));
        lz("Decompress/1", (c, a) => Out(c, Lz77Decompress(c, In(c, a[0], 16))));
        var deflate = r.Scope("Std.Compress.Deflate");
        deflate("Compress/1", (c, a) => Out(c, DeflateCompress(In(c, a[0], 4))));
        deflate("Decompress/1", (c, a) => Out(c, Inflate(c, In(c, a[0], 16))));
        var huffman = r.Scope("Std.Compress.Huffman");
        huffman("Encode/1", (c, a) => Out(c, HuffmanEncode(In(c, a[0], 4))));
        huffman("Decode/1", (c, a) => Out(c, HuffmanDecode(c, In(c, a[0], 16))));
        huffman("CodeLengths/1", (c, a) => c.NewArray([.. CodeLengths(Frequencies(In(c, a[0], 8))).Select(l => Value.FromInt(l))]));
    }

    private static Exception Corrupt(NativeContext c, string format) => c.Throw("FormatError", $"Corrupt {format} data.");

    private static void Guard(NativeContext c, long size)
    {
        if (size > MaxOutput)
        {
            throw c.Throw("ArgumentError", "Decompressed output would exceed 64 MB.");
        }
    }

    #region RLE

    private static byte[] RleEncode(byte[] data)
    {
        var output = new List<byte>();
        for (var i = 0; i < data.Length;)
        {
            var run = 1;
            while (i + run < data.Length && data[i + run] == data[i] && run < 255)
            {
                run++;
            }

            output.Add((byte)run);
            output.Add(data[i]);
            i += run;
        }

        return [.. output];
    }

    private static byte[] RleDecode(NativeContext c, byte[] data)
    {
        if (data.Length % 2 != 0)
        {
            throw Corrupt(c, "RLE");
        }

        var output = new List<byte>();
        for (var i = 0; i < data.Length; i += 2)
        {
            if (data[i] == 0)
            {
                throw Corrupt(c, "RLE");
            }

            Guard(c, output.Count + data[i]);
            output.AddRange(Enumerable.Repeat(data[i + 1], data[i]));
        }

        return [.. output];
    }

    #endregion

    #region LZ77 (our container)

    /// <summary>Greedy longest-match search over 3-byte hash chains, limited for predictable cost.</summary>
    private static (int Distance, int Length) LongestMatch(byte[] data, int position, Dictionary<int, List<int>> chains, int window, int maxLength, int maxChain)
    {
        if (position + 3 > data.Length)
        {
            return (0, 0);
        }

        var key = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
        if (!chains.TryGetValue(key, out var candidates))
        {
            return (0, 0);
        }

        int bestLength = 0, bestDistance = 0, tried = 0;
        for (var k = candidates.Count - 1; k >= 0 && tried < maxChain; k--, tried++)
        {
            var start = candidates[k];
            if (position - start > window)
            {
                break;
            }

            var length = 0;
            while (length < maxLength && position + length < data.Length && data[start + length] == data[position + length])
            {
                length++;
            }

            if (length > bestLength)
            {
                (bestLength, bestDistance) = (length, position - start);
                if (length == maxLength)
                {
                    break;
                }
            }
        }

        return bestLength >= 3 ? (bestDistance, bestLength) : (0, 0);
    }

    private static void Index(byte[] data, int position, Dictionary<int, List<int>> chains)
    {
        if (position + 3 > data.Length)
        {
            return;
        }

        var key = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
        if (!chains.TryGetValue(key, out var list))
        {
            chains[key] = list = [];
        }

        list.Add(position);
    }

    private static byte[] Lz77Compress(byte[] data)
    {
        var output = new List<byte> { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
        var chains = new Dictionary<int, List<int>>();
        var position = 0;
        while (position < data.Length)
        {
            var flagIndex = output.Count;
            output.Add(0);
            for (var bit = 0; bit < 8 && position < data.Length; bit++)
            {
                var (distance, length) = LongestMatch(data, position, chains, 4096, 18, 64);
                if (length >= 3)
                {
                    output[flagIndex] |= (byte)(1 << bit);
                    output.Add((byte)((distance - 1) >> 4));
                    output.Add((byte)((((distance - 1) & 0xF) << 4) | (length - 3)));
                    for (var i = 0; i < length; i++)
                    {
                        Index(data, position + i, chains);
                    }

                    position += length;
                }
                else
                {
                    output.Add(data[position]);
                    Index(data, position, chains);
                    position++;
                }
            }
        }

        return [.. output];
    }

    private static byte[] Lz77Decompress(NativeContext c, byte[] data)
    {
        if (data.Length < 4)
        {
            throw Corrupt(c, "LZ77");
        }

        var size = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        if (size < 0)
        {
            throw Corrupt(c, "LZ77");
        }

        Guard(c, size);
        var output = new List<byte>(size);
        var i = 4;
        while (output.Count < size)
        {
            if (i >= data.Length)
            {
                throw Corrupt(c, "LZ77");
            }

            var flags = data[i++];
            for (var bit = 0; bit < 8 && output.Count < size; bit++)
            {
                if ((flags & (1 << bit)) == 0)
                {
                    if (i >= data.Length)
                    {
                        throw Corrupt(c, "LZ77");
                    }

                    output.Add(data[i++]);
                    continue;
                }

                if (i + 1 >= data.Length)
                {
                    throw Corrupt(c, "LZ77");
                }

                var distance = ((data[i] << 4) | (data[i + 1] >> 4)) + 1;
                var length = (data[i + 1] & 0xF) + 3;
                i += 2;
                if (distance > output.Count)
                {
                    throw Corrupt(c, "LZ77");
                }

                for (var k = 0; k < length; k++)
                {
                    output.Add(output[output.Count - distance]);
                }
            }
        }

        return [.. output];
    }

    #endregion

    #region DEFLATE

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private uint _buffer;
        private int _count;

        public void Write(uint bits, int length)
        {
            _buffer |= bits << _count;
            _count += length;
            while (_count >= 8)
            {
                _bytes.Add((byte)_buffer);
                _buffer >>= 8;
                _count -= 8;
            }
        }

        /// <summary>Huffman codes are defined most-significant bit first.</summary>
        public void WriteCode(uint code, int length)
        {
            uint reversed = 0;
            for (var i = 0; i < length; i++)
            {
                reversed = (reversed << 1) | ((code >> i) & 1);
            }

            Write(reversed, length);
        }

        public byte[] ToArray()
        {
            if (_count > 0)
            {
                _bytes.Add((byte)_buffer);
            }

            return [.. _bytes];
        }
    }

    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    private static void WriteLiteral(BitWriter w, int symbol)
    {
        switch (symbol)
        {
            case <= 143: w.WriteCode((uint)(0x30 + symbol), 8); break;
            case <= 255: w.WriteCode((uint)(0x190 + symbol - 144), 9); break;
            case <= 279: w.WriteCode((uint)(symbol - 256), 7); break;
            default: w.WriteCode((uint)(0xC0 + symbol - 280), 8); break;
        }
    }

    private static int Code(int[] bases, int value)
    {
        var code = bases.Length - 1;
        while (bases[code] > value)
        {
            code--;
        }

        return code;
    }

    private static byte[] DeflateCompress(byte[] data)
    {
        var w = new BitWriter();
        w.Write(1, 1); // final block
        w.Write(1, 2); // fixed Huffman
        var chains = new Dictionary<int, List<int>>();
        var position = 0;
        while (position < data.Length)
        {
            var (distance, length) = LongestMatch(data, position, chains, 32768, 258, 128);
            if (length >= 3)
            {
                var lc = Code(LengthBase, length);
                WriteLiteral(w, 257 + lc);
                w.Write((uint)(length - LengthBase[lc]), LengthExtra[lc]);
                var dc = Code(DistanceBase, distance);
                w.WriteCode((uint)dc, 5);
                w.Write((uint)(distance - DistanceBase[dc]), DistanceExtra[dc]);
                for (var i = 0; i < length; i++)
                {
                    Index(data, position + i, chains);
                }

                position += length;
            }
            else
            {
                WriteLiteral(w, data[position]);
                Index(data, position, chains);
                position++;
            }
        }

        WriteLiteral(w, 256);
        return w.ToArray();
    }

    private static byte[] Inflate(NativeContext c, byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var stream = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                Guard(c, output.Length + read);
                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw Corrupt(c, "DEFLATE");
        }
    }

    #endregion

    #region Huffman (our container)

    private static long[] Frequencies(byte[] data)
    {
        var counts = new long[256];
        foreach (var b in data)
        {
            counts[b]++;
        }

        return counts;
    }

    /// <summary>Code lengths from a Huffman tree built with deterministic tie-breaking (weight, then smallest symbol).</summary>
    internal static int[] CodeLengths(long[] frequencies)
    {
        var lengths = new int[256];
        var used = Enumerable.Range(0, 256).Where(s => frequencies[s] > 0).ToList();
        if (used.Count == 1)
        {
            lengths[used[0]] = 1;
            return lengths;
        }

        var queue = new PriorityQueue<int[], (long Weight, int Order)>();
        foreach (var s in used)
        {
            queue.Enqueue([s], (frequencies[s], s));
        }

        var order = 256;
        while (queue.Count > 1)
        {
            queue.TryDequeue(out var a, out var pa);
            queue.TryDequeue(out var b, out var pb);
            foreach (var s in a!.Concat(b!))
            {
                lengths[s]++;
            }

            queue.Enqueue([.. a, .. b], (pa.Weight + pb.Weight, order++));
        }

        return lengths;
    }

    /// <summary>Canonical codes: sorted by (length, symbol).</summary>
    private static (ulong Code, int Length)[] Canonical(int[] lengths)
    {
        var codes = new (ulong, int)[256];
        ulong code = 0;
        var previous = 0;
        foreach (var s in Enumerable.Range(0, 256).Where(s => lengths[s] > 0).OrderBy(s => lengths[s]).ThenBy(s => s))
        {
            code <<= lengths[s] - previous;
            previous = lengths[s];
            codes[s] = (code, lengths[s]);
            code++;
        }

        return codes;
    }

    private static byte[] HuffmanEncode(byte[] data)
    {
        var lengths = CodeLengths(Frequencies(data));
        var codes = Canonical(lengths);
        var output = new List<byte>(lengths.Select(l => (byte)l))
        {
            (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length,
        };
        byte current = 0;
        var filled = 0;
        foreach (var b in data)
        {
            var (code, length) = codes[b];
            for (var i = length - 1; i >= 0; i--)
            {
                current = (byte)((current << 1) | (int)((code >> i) & 1));
                if (++filled == 8)
                {
                    output.Add(current);
                    (current, filled) = (0, 0);
                }
            }
        }

        if (filled > 0)
        {
            output.Add((byte)(current << (8 - filled)));
        }

        return [.. output];
    }

    private static byte[] HuffmanDecode(NativeContext c, byte[] data)
    {
        if (data.Length < 260)
        {
            throw Corrupt(c, "Huffman");
        }

        var lengths = data.Take(256).Select(b => (int)b).ToArray();
        var size = (data[256] << 24) | (data[257] << 16) | (data[258] << 8) | data[259];
        if (size < 0 || (size > 0 && lengths.All(l => l == 0)) || lengths.Any(l => l > 63))
        {
            throw Corrupt(c, "Huffman");
        }

        Guard(c, size);
        var lookup = new Dictionary<(int, ulong), byte>();
        var codes = Canonical(lengths);
        for (var s = 0; s < 256; s++)
        {
            if (lengths[s] > 0)
            {
                lookup[(codes[s].Length, codes[s].Code)] = (byte)s;
            }
        }

        var output = new byte[size];
        var bitIndex = 260 * 8;
        for (var n = 0; n < size; n++)
        {
            ulong code = 0;
            var length = 0;
            while (true)
            {
                if (bitIndex >= data.Length * 8 || length >= 63)
                {
                    throw Corrupt(c, "Huffman");
                }

                code = (code << 1) | (uint)((data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1);
                bitIndex++;
                length++;
                if (lookup.TryGetValue((length, code), out var symbol))
                {
                    output[n] = symbol;
                    break;
                }
            }
        }

        return output;
    }

    #endregion
}
