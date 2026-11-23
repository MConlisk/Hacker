/// Binary data and protocols (Document 7 §13.7). Reading past the end of the data raises ProtocolError (EPROTO), as a
/// truncated packet would. Cost: 1 step per 16 bytes moved.
module Std.Binary;

enum Endian { Little, Big }

/// A mutable, growable byte buffer (bytes is its immutable counterpart).
class ByteBuffer {
    ByteBuffer();
    ByteBuffer(bytes initial);
    int Length { get; }
    byte this[int index] { get; set; }
    ByteBuffer Append(bytes data);
    @Native("Std.Binary.ByteBuffer.AppendByte") ByteBuffer Append(byte value);
    ByteBuffer Insert(int index, bytes data);
    ByteBuffer Remove(int start, int count);
    void Clear();
    bytes Slice(int start, int count);
    bytes ToBytes();
    string ToString();
}

class BinaryWriter {
    BinaryWriter(Endian endian);
    Endian Endian { get; }
    int Length { get; }
    /// Unsigned writes take the low bits of the int.
    void WriteU8(int value);
    void WriteU16(int value);
    void WriteU32(int value);
    void WriteU64(int value);
    /// Signed writes raise ArgumentError when the value does not fit.
    void WriteI8(int value);
    void WriteI16(int value);
    void WriteI32(int value);
    void WriteI64(int value);
    void WriteF32(float value);
    void WriteF64(float value);
    /// Unsigned LEB128.
    void WriteVarint(int value);
    /// Zig-zag LEB128.
    void WriteSignedVarint(int value);
    void WriteBytes(bytes data);
    /// UTF-8 with a varint length prefix.
    void WriteString(string text);
    /// UTF-8 padded with zeros (or truncated) to exactly length bytes.
    void WriteFixedString(string text, int length);
    bytes ToBytes();
}

class BinaryReader {
    BinaryReader(bytes data, Endian endian);
    Endian Endian { get; }
    int Position { get; set; }
    int Length { get; }
    int Remaining { get; }
    bool AtEnd { get; }
    int ReadU8();
    int ReadU16();
    int ReadU32();
    /// The 64 bits reinterpreted as int (values above 2^63 − 1 come back negative).
    int ReadU64();
    int ReadI8();
    int ReadI16();
    int ReadI32();
    int ReadI64();
    float ReadF32();
    float ReadF64();
    int ReadVarint();
    int ReadSignedVarint();
    bytes ReadBytes(int count);
    string ReadString();
    /// Reads length bytes and drops trailing zero padding.
    string ReadFixedString(int length);
    void Skip(int count);
    bytes PeekBytes(int count);
}

/// Bit-field helpers on 64-bit ints (bit 0 is the least significant).
class Bits {
    static int Extract(int value, int offset, int width);
    static int Insert(int value, int offset, int width, int field);
    static bool Test(int value, int bit);
    static int SetBit(int value, int bit);
    static int ClearBit(int value, int bit);
    /// Reverses the lowest width bits.
    static int Reverse(int value, int width);
    static int ByteSwap16(int value);
    static int ByteSwap32(int value);
    static int ByteSwap64(int value);
    /// Sign-extends the lowest width bits.
    static int SignExtend(int value, int width);
}

/// Python-style struct packing. Format: optional byte order (<, > or !), then codes
/// x (pad) b B (8-bit) h H (16) i I (32) q Q (64) f (float32) d (float64) ?(bool) s (bytes, with a count: "4s"),
/// each optionally preceded by a repeat count ("3H").
class Struct {
    static bytes Pack(string format, params object[] values);
    static object[] Unpack(string format, bytes data);
    static int SizeOf(string format);
}

class Checksum {
    /// CRC-32 (IEEE 802.3, as in zip and PNG).
    static int Crc32(bytes data);
    /// CRC-16/CCITT-FALSE.
    static int Crc16(bytes data);
    static int Adler32(bytes data);
    static int Fletcher16(bytes data);
    /// RFC 1071 ones'-complement checksum (IPv4, TCP, UDP, ICMP headers).
    static int Internet(bytes data);
    /// XOR of every byte.
    static int Xor8(bytes data);
}

class HexDump {
    /// Canonical dump: offset, 16 hex bytes, and the printable characters.
    static string Format(bytes data);
    static string Format(bytes data, int bytesPerLine);
}
