using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

internal sealed class ByteListState(IEnumerable<byte> initial) : NativeState
{
    public List<byte> Data { get; private set; } = [.. initial];

    protected internal override object CaptureSnapshot() => Data.ToArray();

    protected internal override void RestoreSnapshot(object snapshot) => Data = [.. (byte[])snapshot];
}

internal sealed class WriterState(bool bigEndian) : NativeState
{
    public bool BigEndian { get; } = bigEndian;
    public List<byte> Data { get; private set; } = [];

    public void Write(ulong value, int size)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (BigEndian)
        {
            BinaryPrimitives.WriteUInt64BigEndian(buffer, value << (8 * (8 - size)));
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        }

        for (var i = 0; i < size; i++)
        {
            Data.Add(buffer[i]);
        }
    }

    protected internal override object CaptureSnapshot() => Data.ToArray();

    protected internal override void RestoreSnapshot(object snapshot) => Data = [.. (byte[])snapshot];
}

internal sealed class ReaderState(byte[] data, bool bigEndian) : NativeState
{
    public byte[] Data { get; } = data;
    public bool BigEndian { get; } = bigEndian;
    public int Position { get; set; }

    public ReadOnlySpan<byte> Take(NativeContext c, int count)
    {
        if (count < 0 || Position + count > Data.Length)
        {
            throw c.Throw("ProtocolError", $"Truncated data: needed {count} byte(s) at offset {Position}, {Data.Length - Position} left.");
        }

        var span = Data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public ulong Read(NativeContext c, int size)
    {
        var span = Take(c, size);
        ulong value = 0;
        for (var i = 0; i < size; i++)
        {
            value |= (ulong)span[BigEndian ? size - 1 - i : i] << (8 * i);
        }

        return value;
    }

    protected internal override object CaptureSnapshot() => Position;

    protected internal override void RestoreSnapshot(object snapshot) => Position = (int)snapshot;
}

/// <summary>Host implementations of <c>Std.Binary</c>.</summary>
internal static class BinaryNatives
{
    private const string M = "Std.Binary";

    private static Value B(NativeContext c, byte[] data)
    {
        c.Charge(data.Length / 16);
        return Value.FromObject(new BytesObject(data));
    }

    private static byte[] Raw(Value v) => v.As<BytesObject>().Data;

    private static bool Big(Value endian) => endian.Bits == 1;

    public static void Register(NativeRegistry r)
    {
        RegisterBuffer(r.Scope($"{M}.ByteBuffer"));
        RegisterWriter(r.Scope($"{M}.BinaryWriter"));
        RegisterReader(r.Scope($"{M}.BinaryReader"));
        RegisterBits(r.Scope($"{M}.Bits"));
        RegisterStruct(r.Scope($"{M}.Struct"));
        RegisterChecksums(r.Scope($"{M}.Checksum"));
        var h = r.Scope($"{M}.HexDump");
        h("Format/1", (c, a) => Value.FromString(HexDump(c, Raw(a[0]), 16)));
        h("Format/2", (c, a) => Value.FromString(HexDump(c, Raw(a[0]), Int32(c, a[1].Bits, "Bytes per line", 1, 256))));
    }

    #region ByteBuffer

    private static void RegisterBuffer(Action<string, StdFunction> F)
    {
        static ByteListState S(NativeContext c, Value v) => c.State<ByteListState>(v);
        static Value Edit(NativeContext c, Value[] a, Action<List<byte>> edit)
        {
            var s = S(c, a[0]);
            c.Mutating(s);
            edit(s.Data);
            return a[0];
        }

        F("ByteBuffer/0", (c, a) => CollectionNatives.Init(a[0], new ByteListState([])));
        F("ByteBuffer/1", (c, a) => CollectionNatives.Init(a[0], new ByteListState(Raw(a[1]))));
        F("get_Length/0", (c, a) => Value.FromInt(S(c, a[0]).Data.Count));
        F("get_Item/1", (c, a) => Value.FromByte(S(c, a[0]).Data[Index(c, a[1].Bits, S(c, a[0]).Data.Count)]));
        F("set_Item/2", (c, a) =>
        {
            var i = Index(c, a[1].Bits, S(c, a[0]).Data.Count);
            Edit(c, a, d => d[i] = (byte)a[2].Bits);
            return Value.Null;
        });
        F("Append/1", (c, a) =>
        {
            c.Charge(Raw(a[1]).Length / 16);
            return Edit(c, a, d => d.AddRange(Raw(a[1])));
        });
        F("AppendByte", (c, a) => Edit(c, a, d => d.Add((byte)a[1].Bits)));
        F("Insert/2", (c, a) =>
        {
            var length = S(c, a[0]).Data.Count;
            var index = a[1].Bits >= 0 && a[1].Bits <= length ? (int)a[1].Bits : throw c.Throw("IndexError", $"Index {a[1].Bits} is out of range for length {length}.");
            c.Charge((length + Raw(a[2]).Length) / 16);
            return Edit(c, a, d => d.InsertRange(index, Raw(a[2])));
        });
        F("Remove/2", (c, a) =>
        {
            CollectionNatives.CheckRange(c, a[1].Bits, a[2].Bits, S(c, a[0]).Data.Count);
            c.Charge(S(c, a[0]).Data.Count / 16);
            return Edit(c, a, d => d.RemoveRange((int)a[1].Bits, (int)a[2].Bits));
        });
        F("Clear/0", (c, a) =>
        {
            Edit(c, a, d => d.Clear());
            return Value.Null;
        });
        F("Slice/2", (c, a) =>
        {
            var d = S(c, a[0]).Data;
            CollectionNatives.CheckRange(c, a[1].Bits, a[2].Bits, d.Count);
            return B(c, [.. d.GetRange((int)a[1].Bits, (int)a[2].Bits)]);
        });
        F("ToBytes/0", (c, a) => B(c, [.. S(c, a[0]).Data]));
        F("ToString/0", (c, a) => Value.FromString($"ByteBuffer[{S(c, a[0]).Data.Count}]"));
    }

    #endregion

    #region writer and reader

    private static void RegisterWriter(Action<string, StdFunction> F)
    {
        static WriterState S(NativeContext c, Value v) => c.State<WriterState>(v);
        static Value Write(NativeContext c, Value[] a, Action<WriterState> write)
        {
            var s = S(c, a[0]);
            c.Mutating(s);
            write(s);
            return Value.Null;
        }

        static Value Signed(NativeContext c, Value[] a, int size)
        {
            var v = a[1].Bits;
            var bits = size * 8;
            if (bits < 64 && (v < -(1L << (bits - 1)) || v >= 1L << (bits - 1)))
            {
                throw c.Throw("ArgumentError", $"{v} does not fit in a signed {bits}-bit field.");
            }

            return Write(c, a, s => s.Write((ulong)v, size));
        }

        static void Varint(List<byte> data, ulong v)
        {
            do
            {
                var b = (byte)(v & 0x7F);
                v >>= 7;
                data.Add(v != 0 ? (byte)(b | 0x80) : b);
            }
            while (v != 0);
        }

        F("BinaryWriter/1", (c, a) => CollectionNatives.Init(a[0], new WriterState(Big(a[1]))));
        F("get_Endian/0", (c, a) => Value.FromEnum(S(c, a[0]).BigEndian ? 1 : 0, c.Class(M, "Endian")));
        F("get_Length/0", (c, a) => Value.FromInt(S(c, a[0]).Data.Count));
        F("WriteU8/1", (c, a) => Write(c, a, s => s.Write((ulong)a[1].Bits, 1)));
        F("WriteU16/1", (c, a) => Write(c, a, s => s.Write((ulong)a[1].Bits, 2)));
        F("WriteU32/1", (c, a) => Write(c, a, s => s.Write((ulong)a[1].Bits, 4)));
        F("WriteU64/1", (c, a) => Write(c, a, s => s.Write((ulong)a[1].Bits, 8)));
        F("WriteI8/1", (c, a) => Signed(c, a, 1));
        F("WriteI16/1", (c, a) => Signed(c, a, 2));
        F("WriteI32/1", (c, a) => Signed(c, a, 4));
        F("WriteI64/1", (c, a) => Signed(c, a, 8));
        F("WriteF32/1", (c, a) => Write(c, a, s => s.Write(BitConverter.SingleToUInt32Bits((float)MathNatives.D(a[1])), 4)));
        F("WriteF64/1", (c, a) => Write(c, a, s => s.Write(BitConverter.DoubleToUInt64Bits(MathNatives.D(a[1])), 8)));
        F("WriteVarint/1", (c, a) => Write(c, a, s => Varint(s.Data, (ulong)a[1].Bits)));
        F("WriteSignedVarint/1", (c, a) => Write(c, a, s => Varint(s.Data, (ulong)((a[1].Bits << 1) ^ (a[1].Bits >> 63)))));
        F("WriteBytes/1", (c, a) =>
        {
            c.Charge(Raw(a[1]).Length / 16);
            return Write(c, a, s => s.Data.AddRange(Raw(a[1])));
        });
        F("WriteString/1", (c, a) =>
        {
            var utf8 = Encoding.UTF8.GetBytes(a[1].AsString);
            c.Charge(utf8.Length / 16);
            return Write(c, a, s =>
            {
                Varint(s.Data, (ulong)utf8.Length);
                s.Data.AddRange(utf8);
            });
        });
        F("WriteFixedString/2", (c, a) =>
        {
            var length = Int32(c, a[2].Bits, "Length", 0, 1 << 24);
            var utf8 = Encoding.UTF8.GetBytes(a[1].AsString);
            var field = new byte[length];
            System.Array.Copy(utf8, field, Math.Min(length, utf8.Length));
            return Write(c, a, s => s.Data.AddRange(field));
        });
        F("ToBytes/0", (c, a) => B(c, [.. S(c, a[0]).Data]));
    }

    private static void RegisterReader(Action<string, StdFunction> F)
    {
        static ReaderState S(NativeContext c, Value v) => c.State<ReaderState>(v);
        static ReaderState M(NativeContext c, Value v)
        {
            var s = S(c, v);
            c.Mutating(s);
            return s;
        }

        static long SignExtend(ulong value, int size) => size == 8 ? (long)value : ((long)(value << (64 - (8 * size)))) >> (64 - (8 * size));
        static ulong Varint(NativeContext c, ReaderState s)
        {
            ulong result = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                var b = s.Take(c, 1)[0];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }
            }

            throw c.Throw("ProtocolError", "Varint is longer than 10 bytes.");
        }

        F("BinaryReader/2", (c, a) => CollectionNatives.Init(a[0], new ReaderState(Raw(a[1]), Big(a[2]))));
        F("get_Endian/0", (c, a) => Value.FromEnum(S(c, a[0]).BigEndian ? 1 : 0, c.Class(Std(), "Endian")));
        F("get_Position/0", (c, a) => Value.FromInt(S(c, a[0]).Position));
        F("set_Position/1", (c, a) =>
        {
            var s = S(c, a[0]);
            var p = a[1].Bits >= 0 && a[1].Bits <= s.Data.Length ? (int)a[1].Bits : throw c.Throw("IndexError", $"Position {a[1].Bits} is outside 0..{s.Data.Length}.");
            M(c, a[0]).Position = p;
            return Value.Null;
        });
        F("get_Length/0", (c, a) => Value.FromInt(S(c, a[0]).Data.Length));
        F("get_Remaining/0", (c, a) => Value.FromInt(S(c, a[0]).Data.Length - S(c, a[0]).Position));
        F("get_AtEnd/0", (c, a) => Value.FromBool(S(c, a[0]).Position >= S(c, a[0]).Data.Length));
        foreach (var (name, size) in new[] { ("8", 1), ("16", 2), ("32", 4), ("64", 8) })
        {
            F($"ReadU{name}/0", (c, a) => Value.FromInt((long)M(c, a[0]).Read(c, size)));
            F($"ReadI{name}/0", (c, a) => Value.FromInt(SignExtend(M(c, a[0]).Read(c, size), size)));
        }

        F("ReadF32/0", (c, a) => Value.FromFloat(BitConverter.UInt32BitsToSingle((uint)M(c, a[0]).Read(c, 4))));
        F("ReadF64/0", (c, a) => Value.FromFloat(BitConverter.UInt64BitsToDouble(M(c, a[0]).Read(c, 8))));
        F("ReadVarint/0", (c, a) => Value.FromInt((long)Varint(c, M(c, a[0]))));
        F("ReadSignedVarint/0", (c, a) =>
        {
            var v = Varint(c, M(c, a[0]));
            return Value.FromInt((long)(v >> 1) ^ -(long)(v & 1));
        });
        F("ReadBytes/1", (c, a) => B(c, M(c, a[0]).Take(c, Int32(c, a[1].Bits, "Count")).ToArray()));
        F("ReadString/0", (c, a) =>
        {
            var s = M(c, a[0]);
            var length = Varint(c, s);
            return length > int.MaxValue ? throw c.Throw("ProtocolError", "String length is too large.") : Value.FromString(Encoding.UTF8.GetString(s.Take(c, (int)length)));
        });
        F("ReadFixedString/1", (c, a) => Value.FromString(Encoding.UTF8.GetString(M(c, a[0]).Take(c, Int32(c, a[1].Bits, "Length"))).TrimEnd('\0')));
        F("Skip/1", (c, a) =>
        {
            M(c, a[0]).Take(c, Int32(c, a[1].Bits, "Count"));
            return Value.Null;
        });
        F("PeekBytes/1", (c, a) =>
        {
            var s = S(c, a[0]);
            var n = Int32(c, a[1].Bits, "Count");
            return n <= s.Data.Length - s.Position ? B(c, s.Data.AsSpan(s.Position, n).ToArray()) : throw c.Throw("ProtocolError", "Not enough data to peek.");
        });

        static string Std() => "Std.Binary";
    }

    #endregion

    #region Bits, Struct, checksums, dump

    private static void RegisterBits(Action<string, StdFunction> F)
    {
        static ulong Mask(int width) => width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
        static (int Offset, int Width) Field(NativeContext c, long offset, long width)
        {
            if (offset < 0 || width < 0 || offset + width > 64)
            {
                throw c.Throw("ArgumentError", $"Bit field ({offset}, {width}) does not fit in 64 bits.");
            }

            return ((int)offset, (int)width);
        }

        static int Bit(NativeContext c, long bit) => bit is >= 0 and < 64 ? (int)bit : throw c.Throw("ArgumentError", $"Bit {bit} is outside 0..63.");

        F("Extract/3", (c, a) =>
        {
            var (o, w) = Field(c, a[1].Bits, a[2].Bits);
            return Value.FromInt((long)(((ulong)a[0].Bits >> o) & Mask(w)));
        });
        F("Insert/4", (c, a) =>
        {
            var (o, w) = Field(c, a[1].Bits, a[2].Bits);
            var mask = Mask(w) << o;
            return Value.FromInt((long)(((ulong)a[0].Bits & ~mask) | (((ulong)a[3].Bits << o) & mask)));
        });
        F("Test/2", (c, a) => Value.FromBool(((ulong)a[0].Bits & (1UL << Bit(c, a[1].Bits))) != 0));
        F("SetBit/2", (c, a) => Value.FromInt((long)((ulong)a[0].Bits | (1UL << Bit(c, a[1].Bits)))));
        F("ClearBit/2", (c, a) => Value.FromInt((long)((ulong)a[0].Bits & ~(1UL << Bit(c, a[1].Bits)))));
        F("Reverse/2", (c, a) =>
        {
            var (_, w) = Field(c, 0, a[1].Bits);
            ulong v = (ulong)a[0].Bits, result = 0;
            for (var i = 0; i < w; i++)
            {
                result = (result << 1) | ((v >> i) & 1);
            }

            return Value.FromInt((long)result);
        });
        F("ByteSwap16/1", (c, a) => Value.FromInt(BinaryPrimitives.ReverseEndianness((ushort)a[0].Bits)));
        F("ByteSwap32/1", (c, a) => Value.FromInt(BinaryPrimitives.ReverseEndianness((uint)a[0].Bits)));
        F("ByteSwap64/1", (c, a) => Value.FromInt((long)BinaryPrimitives.ReverseEndianness((ulong)a[0].Bits)));
        F("SignExtend/2", (c, a) =>
        {
            var w = (int)Math.Clamp(a[1].Bits, 1, 64);
            return Value.FromInt((a[0].Bits << (64 - w)) >> (64 - w));
        });
    }

    private static List<(char Code, int Count)> ParseFormat(NativeContext c, string format, out bool bigEndian)
    {
        bigEndian = false;
        var items = new List<(char, int)>();
        var i = 0;
        if (format.Length > 0 && format[0] is '<' or '>' or '!' or '=' or '@')
        {
            bigEndian = format[0] is '>' or '!';
            i = 1;
        }

        while (i < format.Length)
        {
            if (char.IsWhiteSpace(format[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < format.Length && char.IsAsciiDigit(format[i]))
            {
                i++;
            }

            var count = i > start ? int.Parse(format.AsSpan(start, i - start), CultureInfo.InvariantCulture) : 1;
            if (i >= format.Length || "xbBhHiIqQfd?s".IndexOf(format[i], StringComparison.Ordinal) < 0)
            {
                throw c.Throw("FormatError", $"Invalid struct format '{format}' at position {i}.");
            }

            items.Add((format[i], count));
            i++;
        }

        return items;
    }

    private static int SizeOf(char code) => code switch
    {
        'x' or 'b' or 'B' or '?' or 's' => 1,
        'h' or 'H' => 2,
        'i' or 'I' or 'f' => 4,
        _ => 8,
    };

    private static void RegisterStruct(Action<string, StdFunction> F)
    {
        F("SizeOf/1", (c, a) => Value.FromInt(ParseFormat(c, a[0].AsString, out _).Sum(x => (long)SizeOf(x.Code) * x.Count)));
        F("Pack/2", (c, a) =>
        {
            var format = ParseFormat(c, a[0].AsString, out var big);
            var values = Items(a[1]);
            var writer = new WriterState(big);
            var index = 0;
            Value Next()
            {
                return index < values.Length ? values[index++] : throw c.Throw("ArgumentError", "Struct.Pack has fewer values than the format needs.");
            }

            foreach (var (code, count) in format)
            {
                if (code == 's')
                {
                    var data = Next().Object is BytesObject b ? b.Data : throw c.Throw("ArgumentError", "'s' expects a bytes value.");
                    var field = new byte[count];
                    System.Array.Copy(data, field, Math.Min(count, data.Length));
                    writer.Data.AddRange(field);
                    continue;
                }

                for (var k = 0; k < count; k++)
                {
                    switch (code)
                    {
                        case 'x':
                            writer.Data.Add(0);
                            break;
                        case 'f':
                            writer.Write(BitConverter.SingleToUInt32Bits((float)MathNatives.D(Next())), 4);
                            break;
                        case 'd':
                            writer.Write(BitConverter.DoubleToUInt64Bits(MathNatives.D(Next())), 8);
                            break;
                        case '?':
                            writer.Write(Next().AsBool ? 1UL : 0UL, 1);
                            break;
                        default:
                            var v = Next();
                            if (v.Tag is not (ValueTag.Int or ValueTag.Byte or ValueTag.Char))
                            {
                                throw c.Throw("ArgumentError", $"'{code}' expects an integer.");
                            }

                            writer.Write((ulong)v.Bits, SizeOf(code));
                            break;
                    }
                }
            }

            if (index != values.Length)
            {
                throw c.Throw("ArgumentError", "Struct.Pack has more values than the format uses.");
            }

            return B(c, [.. writer.Data]);
        });
        F("Unpack/2", (c, a) =>
        {
            var format = ParseFormat(c, a[0].AsString, out var big);
            var reader = new ReaderState(Raw(a[1]), big);
            var results = new List<Value>();
            foreach (var (code, count) in format)
            {
                if (code == 's')
                {
                    results.Add(B(c, reader.Take(c, count).ToArray()));
                    continue;
                }

                for (var k = 0; k < count; k++)
                {
                    switch (code)
                    {
                        case 'x':
                            reader.Take(c, 1);
                            break;
                        case 'f':
                            results.Add(Value.FromFloat(BitConverter.UInt32BitsToSingle((uint)reader.Read(c, 4))));
                            break;
                        case 'd':
                            results.Add(Value.FromFloat(BitConverter.UInt64BitsToDouble(reader.Read(c, 8))));
                            break;
                        case '?':
                            results.Add(Value.FromBool(reader.Read(c, 1) != 0));
                            break;
                        default:
                            var size = SizeOf(code);
                            var raw = reader.Read(c, size);
                            results.Add(Value.FromInt(char.IsLower(code) && size < 8 ? ((long)(raw << (64 - (8 * size)))) >> (64 - (8 * size)) : (long)raw));
                            break;
                    }
                }
            }

            return c.NewArray([.. results]);
        });
    }

    private static readonly Lazy<uint[]> Crc32Table = new(() => [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }

        return c;
    })]);

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var table = Crc32Table.Value;
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static void RegisterChecksums(Action<string, StdFunction> F)
    {
        static byte[] Data(NativeContext c, Value v)
        {
            var data = Raw(v);
            c.Charge(data.Length / 16);
            return data;
        }

        F("Crc32/1", (c, a) => Value.FromInt(Crc32(Data(c, a[0]))));
        F("Crc16/1", (c, a) =>
        {
            ushort crc = 0xFFFF;
            foreach (var b in Data(c, a[0]))
            {
                crc ^= (ushort)(b << 8);
                for (var i = 0; i < 8; i++)
                {
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                }
            }

            return Value.FromInt(crc);
        });
        F("Adler32/1", (c, a) =>
        {
            uint s1 = 1, s2 = 0;
            foreach (var b in Data(c, a[0]))
            {
                s1 = (s1 + b) % 65521;
                s2 = (s2 + s1) % 65521;
            }

            return Value.FromInt((s2 << 16) | s1);
        });
        F("Fletcher16/1", (c, a) =>
        {
            uint s1 = 0, s2 = 0;
            foreach (var b in Data(c, a[0]))
            {
                s1 = (s1 + b) % 255;
                s2 = (s2 + s1) % 255;
            }

            return Value.FromInt((s2 << 8) | s1);
        });
        F("Internet/1", (c, a) =>
        {
            var data = Data(c, a[0]);
            ulong sum = 0;
            for (var i = 0; i < data.Length; i += 2)
            {
                sum += (ulong)((data[i] << 8) | (i + 1 < data.Length ? data[i + 1] : 0));
            }

            while (sum >> 16 != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }

            return Value.FromInt((long)(~sum & 0xFFFF));
        });
        F("Xor8/1", (c, a) => Value.FromInt(Data(c, a[0]).Aggregate(0, (x, b) => x ^ b)));
    }

    internal static string HexDump(NativeContext c, byte[] data, int width)
    {
        c.Charge(1 + (data.Length / 8));
        var sb = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += width)
        {
            var line = data.AsSpan(offset, Math.Min(width, data.Length - offset));
            sb.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < width; i++)
            {
                sb.Append(i < line.Length ? line[i].ToString("x2", CultureInfo.InvariantCulture) + " " : "   ");
                if (i == (width / 2) - 1)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(" |");
            foreach (var b in line)
            {
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.Append("|\n");
        }

        return sb.ToString();
    }

    #endregion
}
