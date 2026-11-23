/// Deterministic compression (Document 7 §13.7): the same input always produces the same bytes on every machine.
/// Decompression raises FormatError on corrupt input and ArgumentError when the output would exceed 64 MB.
/// Cost: about 1 step per 4 input bytes to compress and per 16 output bytes to decompress.
module Std.Compress;

/// Run-length encoding: (count, byte) pairs, runs of up to 255.
class Rle {
    static bytes Encode(bytes data);
    static bytes Decode(bytes data);
}

/// LZ77 with a 4 KB window: a flag byte per 8 tokens; a literal is 1 byte, a match 2 bytes (12-bit distance, 4-bit length − 3).
class Lz77 {
    static bytes Compress(bytes data);
    static bytes Decompress(bytes data);
}

/// Raw DEFLATE (RFC 1951): readable by any inflater. Compression emits fixed-Huffman blocks with a 32 KB window.
class Deflate {
    static bytes Compress(bytes data);
    static bytes Decompress(bytes data);
}

/// Canonical Huffman coding of bytes: a 256-byte table of code lengths, the 4-byte length of the input, then the bit stream.
class Huffman {
    static bytes Encode(bytes data);
    static bytes Decode(bytes data);
    /// The code length chosen for each byte value (0 = absent).
    static int[] CodeLengths(bytes data);
}
