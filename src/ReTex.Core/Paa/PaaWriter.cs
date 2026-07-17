using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace ReTex.Core.Paa;

/// <summary>Writes game-ready BC1/BC3 PAA files with a complete mip chain.</summary>
public static class PaaWriter
{
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> bgra,
        PaaWriteOptions options, CancellationToken cancellationToken = default,
        Action<double>? reportProgress = null)
    {
        using var stream = File.Create(path);
        Write(stream, width, height, bgra, options, cancellationToken, reportProgress);
    }

    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> bgra,
        PaaWriteOptions options, CancellationToken cancellationToken = default,
        Action<double>? reportProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(width, height, bgra.Length);
        var format = options.Format;
        bool fractionalAlpha = HasFractionalAlpha(bgra);
        bool anyAlpha = HasAnyAlpha(bgra);
        if (options.UpgradeForAlpha && format == PaaFormat.Dxt1 && fractionalAlpha)
            format = PaaFormat.Dxt5;

        var levels = BuildMipChain(width, height, bgra.ToArray(), options.GenerateMipMaps, cancellationToken);
        if (levels.Count > 16) levels.RemoveRange(16, levels.Count - 16);
        reportProgress?.Invoke(0.05);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        bw.Write(format == PaaFormat.Dxt1 ? (ushort)0xFF01 : (ushort)0xFF05);
        WriteTag(bw, "CGVA", AverageColor(levels[0].Pixels));
        WriteTag(bw, "CXAM", new byte[] { 255, 255, 255, 255 });
        if (anyAlpha) WriteTag(bw, "GALF", BitConverter.GetBytes(format == PaaFormat.Dxt1 ? 2u : 1u));

        bw.Write(System.Text.Encoding.ASCII.GetBytes("GGATSFFO"));
        bw.Write(64);
        long offsetsPos = ms.Position;
        for (int i = 0; i < 16; i++) bw.Write(0u);
        bw.Write((ushort)0); // palette count

        var offsets = new uint[16];
        var compression = format == PaaFormat.Dxt5 ? CompressionFormat.Bc3
            : anyAlpha ? CompressionFormat.Bc1WithAlpha : CompressionFormat.Bc1;
        var encoder = new BcEncoder(compression);
        encoder.OutputOptions.Quality = CompressionQuality.BestQuality;
        encoder.OutputOptions.GenerateMipMaps = false;
        for (int i = 0; i < levels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var level = levels[i];
            offsets[i] = checked((uint)ms.Position);
            byte[] encoded = encoder.EncodeToRawBytes(level.Pixels, level.Width, level.Height, PixelFormat.Bgra32)[0];
            bw.Write((ushort)level.Width);
            bw.Write((ushort)level.Height);
            WriteU24(bw, encoded.Length);
            bw.Write(encoded);
            reportProgress?.Invoke(0.05 + 0.9 * (i + 1d) / levels.Count);
        }
        bw.Write((ushort)0);
        bw.Write((ushort)0);

        long end = ms.Position;
        ms.Position = offsetsPos;
        foreach (uint offset in offsets) bw.Write(offset);
        ms.Position = 0;
        cancellationToken.ThrowIfCancellationRequested();
        ms.CopyTo(output);
        reportProgress?.Invoke(1);
    }

    private static void Validate(int width, int height, int bytes)
    {
        if (width < 4 || height < 4 || !IsPowerOfTwo(width) || !IsPowerOfTwo(height))
            throw new ArgumentException("PAA dimensions must be power-of-two and at least 4x4.");
        if (bytes != checked(width * height * 4))
            throw new ArgumentException("BGRA buffer length does not match the image dimensions.");
    }

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;
    private static bool HasFractionalAlpha(ReadOnlySpan<byte> pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] is > 0 and < 255) return true;
        return false;
    }
    private static bool HasAnyAlpha(ReadOnlySpan<byte> pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] < 255) return true;
        return false;
    }

    private static List<Mip> BuildMipChain(int width, int height, byte[] pixels, bool all,
        CancellationToken cancellationToken)
    {
        var result = new List<Mip> { new(width, height, pixels) };
        while (all && (width > 2 || height > 2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int nextWidth = Math.Max(2, width / 2), nextHeight = Math.Max(2, height / 2);
            pixels = DownsampleLinear(pixels, width, height, nextWidth, nextHeight);
            width = nextWidth; height = nextHeight;
            result.Add(new Mip(width, height, pixels));
        }
        return result;
    }

    private static byte[] DownsampleLinear(byte[] source, int sw, int sh, int dw, int dh)
    {
        var dest = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        for (int x = 0; x < dw; x++)
        {
            int di = (y * dw + x) * 4;
            for (int c = 0; c < 3; c++)
            {
                double sum = 0;
                for (int oy = 0; oy < 2; oy++)
                for (int ox = 0; ox < 2; ox++)
                {
                    int sx = Math.Min(sw - 1, x * 2 + ox), sy = Math.Min(sh - 1, y * 2 + oy);
                    double srgb = source[(sy * sw + sx) * 4 + c] / 255.0;
                    sum += Math.Pow(srgb, 2.2);
                }
                dest[di + c] = (byte)Math.Clamp(Math.Round(Math.Pow(sum / 4, 1 / 2.2) * 255), 0, 255);
            }
            int alpha = 0;
            for (int oy = 0; oy < 2; oy++)
            for (int ox = 0; ox < 2; ox++)
                alpha += source[(Math.Min(sh - 1, y * 2 + oy) * sw + Math.Min(sw - 1, x * 2 + ox)) * 4 + 3];
            dest[di + 3] = (byte)((alpha + 2) / 4);
        }
        return dest;
    }

    private static byte[] AverageColor(byte[] pixels)
    {
        long b = 0, g = 0, r = 0, a = 0;
        int count = pixels.Length / 4;
        for (int i = 0; i < pixels.Length; i += 4)
        { b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; a += pixels[i + 3]; }
        return new[] { (byte)(b / count), (byte)(g / count), (byte)(r / count), (byte)(a / count) };
    }

    private static void WriteTag(BinaryWriter bw, string reversedName, byte[] data)
    {
        bw.Write(System.Text.Encoding.ASCII.GetBytes("GGAT" + reversedName));
        bw.Write(data.Length);
        bw.Write(data);
    }

    private static void WriteU24(BinaryWriter bw, int value)
    {
        if ((uint)value > 0xFFFFFF) throw new InvalidDataException("PAA mipmap is too large.");
        bw.Write((byte)value); bw.Write((byte)(value >> 8)); bw.Write((byte)(value >> 16));
    }

    private sealed record Mip(int Width, int Height, byte[] Pixels);
}
