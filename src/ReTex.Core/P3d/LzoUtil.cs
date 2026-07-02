using lzo.net;

namespace ReTex.Core.P3d;

/// <summary>LZO1x decompression for ODOL p3d compressed arrays (the "1024-byte rule" arrays).
/// Unlike PAA mips, p3d arrays don't store the compressed length, so callers know only the
/// expected output size; this returns how many input bytes were consumed so the caller can
/// advance its cursor to the next array.</summary>
public static class LzoUtil
{
    /// <summary>Decompresses exactly <paramref name="outLen"/> bytes of LZO1x data starting at
    /// <paramref name="offset"/>. <paramref name="consumed"/> receives the number of input bytes
    /// read (so <c>offset + consumed</c> is where the next field begins).</summary>
    public static byte[] Decompress(byte[] src, int offset, int outLen, out int consumed)
    {
        using var ms = new MemoryStream(src, offset, src.Length - offset);
        using var lzo = new LzoStream(ms, System.IO.Compression.CompressionMode.Decompress);
        var outBuf = new byte[outLen];
        int read = 0;
        while (read < outLen)
        {
            int n = lzo.Read(outBuf, read, outLen - read);
            if (n <= 0) break;
            read += n;
        }
        if (read < outLen) throw new InvalidDataException($"LZO underflow: got {read} of {outLen} bytes at offset {offset}.");
        consumed = (int)ms.Position;
        return outBuf;
    }

    /// <summary>Best-effort: decompress until the LZO stream ends, returning however many bytes
    /// were produced (up to <paramref name="cap"/>). Diagnostic only.</summary>
    public static byte[] DecompressAll(byte[] src, int offset, int cap, out int consumed)
    {
        using var ms = new MemoryStream(src, offset, src.Length - offset);
        using var lzo = new LzoStream(ms, System.IO.Compression.CompressionMode.Decompress);
        var outBuf = new byte[cap];
        int read = 0;
        while (read < cap)
        {
            int n = lzo.Read(outBuf, read, cap - read);
            if (n <= 0) break;
            read += n;
        }
        consumed = (int)ms.Position;
        Array.Resize(ref outBuf, read);
        return outBuf;
    }
}
