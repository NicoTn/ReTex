using lzo.net;

namespace ReTex.Core.Paa;

/// <summary>
/// Decodes an Arma .paa texture to a 32-bit BGRA bitmap (top-down). Supports the common
/// DXT1 (0xFF01) and DXT5 (0xFF05) formats with LZO-compressed mipmaps. Picks the largest
/// mipmap it can decode (falling back to smaller, uncompressed mips if LZO fails).
/// </summary>
public sealed class PaaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; } // length = Width*Height*4

    private PaaImage(int w, int h, byte[] bgra) { Width = w; Height = h; Bgra = bgra; }

    private const ushort TypeDxt1 = 0xFF01;
    private const ushort TypeDxt5 = 0xFF05;

    public static PaaImage LoadFile(string path) => Load(File.ReadAllBytes(path));

    public static PaaImage Load(byte[] d)
    {
        ushort type = BitConverter.ToUInt16(d, 0);
        if (type != TypeDxt1 && type != TypeDxt5)
            throw new NotSupportedException($"Unsupported PAA type 0x{type:X4} (only DXT1/DXT5).");

        // Parse TAGGs to find the mipmap offset table (OFFS).
        int pos = 2;
        uint[] offsets = Array.Empty<uint>();
        while (pos + 8 <= d.Length && d[pos] == 'G' && d[pos + 1] == 'G' && d[pos + 2] == 'A' && d[pos + 3] == 'T')
        {
            string name = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4); // reversed, e.g. "SFFO"
            int len = BitConverter.ToInt32(d, pos + 8);
            int payload = pos + 12;
            if (name == "SFFO")
            {
                offsets = new uint[len / 4];
                for (int i = 0; i < offsets.Length; i++) offsets[i] = BitConverter.ToUInt32(d, payload + i * 4);
            }
            pos = payload + len;
        }

        // Build the candidate mipmap list (largest first via OFFS).
        var mips = new List<(int w, int h, bool lzo, int dataOff, int dataLen)>();
        foreach (var off in offsets)
        {
            if (off == 0 || off + 7 > d.Length) continue;
            int p = (int)off;
            int w = BitConverter.ToUInt16(d, p);
            int h = BitConverter.ToUInt16(d, p + 2);
            bool lzo = (w & 0x8000) != 0;
            w &= 0x7FFF;
            int dlen = d[p + 4] | (d[p + 5] << 8) | (d[p + 6] << 16);
            if (w == 0 || h == 0) continue;
            mips.Add((w, h, lzo, p + 7, dlen));
        }
        if (mips.Count == 0) throw new InvalidDataException("No mipmaps found in PAA.");

        int blockBytes = type == TypeDxt1 ? 8 : 16;

        foreach (var m in mips.OrderByDescending(m => m.w * m.h))
        {
            int expected = Math.Max(1, (m.w + 3) / 4) * Math.Max(1, (m.h + 3) / 4) * blockBytes;
            byte[]? dxt = null;

            if (!m.lzo)
            {
                dxt = new byte[m.dataLen];
                Array.Copy(d, m.dataOff, dxt, 0, m.dataLen);
            }
            else
            {
                try { dxt = LzoDecompress(d, m.dataOff, m.dataLen, expected); }
                catch { dxt = null; } // fall through to a smaller / uncompressed mip
            }

            if (dxt is null || dxt.Length < expected) continue;

            var bgra = type == TypeDxt1 ? DecodeDxt1(dxt, m.w, m.h) : DecodeDxt5(dxt, m.w, m.h);
            return new PaaImage(m.w, m.h, bgra);
        }

        throw new InvalidDataException("Could not decode any mipmap (LZO unavailable for compressed mips).");
    }

    private static byte[] LzoDecompress(byte[] src, int offset, int len, int outLen)
    {
        using var ms = new MemoryStream(src, offset, len);
        using var lzo = new LzoStream(ms, System.IO.Compression.CompressionMode.Decompress);
        var outBuf = new byte[outLen];
        int read = 0;
        while (read < outLen)
        {
            int n = lzo.Read(outBuf, read, outLen - read);
            if (n <= 0) break;
            read += n;
        }
        if (read < outLen) throw new InvalidDataException("LZO underflow.");
        return outBuf;
    }

    // --- DXT decoders -> BGRA32 top-down ---

    private static byte[] DecodeDxt1(byte[] s, int w, int h)
    {
        var o = new byte[w * h * 4];
        int bx = (w + 3) / 4, by = (h + 3) / 4, k = 0;
        var c = new byte[4 * 4];
        for (int byi = 0; byi < by; byi++)
            for (int bxi = 0; bxi < bx; bxi++, k += 8)
            {
                BuildDxtColors(s, k, c, dxt1: true);
                uint idx = BitConverter.ToUInt32(s, k + 4);
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int ci = (int)((idx >> (2 * (py * 4 + px))) & 3);
                        PutPixel(o, w, h, bxi * 4 + px, byi * 4 + py, c[ci * 4], c[ci * 4 + 1], c[ci * 4 + 2], c[ci * 4 + 3]);
                    }
            }
        return o;
    }

    private static byte[] DecodeDxt5(byte[] s, int w, int h)
    {
        var o = new byte[w * h * 4];
        int bx = (w + 3) / 4, by = (h + 3) / 4, k = 0;
        var c = new byte[4 * 4];
        var a = new byte[8];
        for (int byi = 0; byi < by; byi++)
            for (int bxi = 0; bxi < bx; bxi++, k += 16)
            {
                a[0] = s[k]; a[1] = s[k + 1];
                if (a[0] > a[1])
                    for (int i = 1; i < 7; i++) a[i + 1] = (byte)(((7 - i) * a[0] + i * a[1]) / 7);
                else
                {
                    for (int i = 1; i < 5; i++) a[i + 1] = (byte)(((5 - i) * a[0] + i * a[1]) / 5);
                    a[6] = 0; a[7] = 255;
                }
                ulong abits = 0;
                for (int i = 0; i < 6; i++) abits |= (ulong)s[k + 2 + i] << (8 * i);

                BuildDxtColors(s, k + 8, c, dxt1: false);
                uint idx = BitConverter.ToUInt32(s, k + 12);
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int p = py * 4 + px;
                        int ci = (int)((idx >> (2 * p)) & 3);
                        int ai = (int)((abits >> (3 * p)) & 7);
                        PutPixel(o, w, h, bxi * 4 + px, byi * 4 + py, c[ci * 4], c[ci * 4 + 1], c[ci * 4 + 2], a[ai]);
                    }
            }
        return o;
    }

    private static void BuildDxtColors(byte[] s, int k, byte[] c, bool dxt1)
    {
        ushort c0 = BitConverter.ToUInt16(s, k), c1 = BitConverter.ToUInt16(s, k + 2);
        Rgb565(c0, out byte r0, out byte g0, out byte b0);
        Rgb565(c1, out byte r1, out byte g1, out byte b1);
        c[0] = b0; c[1] = g0; c[2] = r0; c[3] = 255;
        c[4] = b1; c[5] = g1; c[6] = r1; c[7] = 255;
        if (!dxt1 || c0 > c1)
        {
            c[8] = (byte)((2 * b0 + b1) / 3); c[9] = (byte)((2 * g0 + g1) / 3); c[10] = (byte)((2 * r0 + r1) / 3); c[11] = 255;
            c[12] = (byte)((b0 + 2 * b1) / 3); c[13] = (byte)((g0 + 2 * g1) / 3); c[14] = (byte)((r0 + 2 * r1) / 3); c[15] = 255;
        }
        else
        {
            c[8] = (byte)((b0 + b1) / 2); c[9] = (byte)((g0 + g1) / 2); c[10] = (byte)((r0 + r1) / 2); c[11] = 255;
            c[12] = 0; c[13] = 0; c[14] = 0; c[15] = 0;
        }
    }

    private static void Rgb565(ushort v, out byte r, out byte g, out byte b)
    {
        r = (byte)(((v >> 11) & 31) * 255 / 31);
        g = (byte)(((v >> 5) & 63) * 255 / 63);
        b = (byte)((v & 31) * 255 / 31);
    }

    private static void PutPixel(byte[] o, int w, int h, int x, int y, byte b, byte g, byte r, byte a)
    {
        if (x >= w || y >= h) return;
        int i = (y * w + x) * 4;
        o[i] = b; o[i + 1] = g; o[i + 2] = r; o[i + 3] = a;
    }
}
