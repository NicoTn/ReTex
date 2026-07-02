using System.IO.Compression;

namespace Probe;

/// <summary>Minimal RGBA PNG encoder (no external deps): IHDR + one IDAT (zlib-wrapped raw
/// deflate of filter-0 scanlines) + IEND. Used by the headless renderer to dump renders to disk.</summary>
public static class Png
{
    public static void Write(string path, int w, int h, byte[] rgba)
    {
        using var fs = File.Create(path);
        Span<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        fs.Write(sig);

        // IHDR
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w);
        WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type 6 = RGBA
        Chunk(fs, "IHDR", ihdr);

        // Raw image data: each row prefixed with a filter byte (0 = none).
        var raw = new byte[h * (w * 4 + 1)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            Array.Copy(rgba, y * w * 4, raw, o, w * 4);
            o += w * 4;
        }

        // zlib wrapper: 0x78 0x01 + raw deflate + adler32.
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x01);
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(raw, 0, raw.Length);
        WriteBEStream(ms, Adler32(raw));
        Chunk(fs, "IDAT", ms.ToArray());

        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, (uint)data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        uint crc = Crc32(t, data);
        var c = new byte[4]; WriteBE(c, 0, crc); s.Write(c);
    }

    private static void WriteBE(byte[] b, int i, uint v)
    { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }

    private static void WriteBEStream(Stream s, uint v)
    { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

    private static uint Adler32(byte[] d)
    {
        uint a = 1, b = 0;
        foreach (var x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrc();
    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
