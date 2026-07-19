using ReTex.Core.Paa;

namespace ReTex.Core.Tests;

public sealed class PaaWriterTests
{
    [Fact] public void Dxt1RoundTripProducesReadableMipmappedPaa()
    {
        var pixels = Solid(16, 8, 30, 80, 180, 255); using var stream = new MemoryStream();
        PaaWriter.Write(stream, 16, 8, pixels, new PaaWriteOptions(PaaFormat.Dxt1));
        var image = PaaImage.Load(stream.ToArray());
        Assert.Equal(PaaFormat.Dxt1, image.Format); Assert.Equal(16, image.Width); Assert.Equal(8, image.Height);
        Assert.InRange(image.Bgra[0], 20, 40); Assert.InRange(image.Bgra[2], 165, 195);
    }

    [Fact] public void FractionalAlphaUpgradesDxt1ToDxt5()
    {
        var pixels = Solid(8, 8, 10, 20, 30, 255); pixels[3] = 120; using var stream = new MemoryStream();
        PaaWriter.Write(stream, 8, 8, pixels, new PaaWriteOptions(PaaFormat.Dxt1));
        Assert.Equal(PaaFormat.Dxt5, PaaImage.Load(stream.ToArray()).Format);
    }

    [Fact] public void RejectsNonPowerOfTwoDimensions() =>
        Assert.Throws<ArgumentException>(() => PaaWriter.Write(new MemoryStream(), 10, 8, new byte[10 * 8 * 4], new PaaWriteOptions(PaaFormat.Dxt1)));

    [Fact] public void BinaryAlphaRemainsDxt1()
    {
        var pixels = Solid(8, 8, 10, 20, 30, 255); pixels[3] = 0; using var stream = new MemoryStream();
        PaaWriter.Write(stream, 8, 8, pixels, new PaaWriteOptions(PaaFormat.Dxt1));
        var image = PaaImage.Load(stream.ToArray());
        Assert.Equal(PaaFormat.Dxt1, image.Format); Assert.Equal((byte)0, image.Bgra[3]);
    }

    [Fact] public void WritesArmaTagsAndIncreasingMipmapOffsets()
    {
        var pixels = Solid(16, 8, 10, 20, 30, 120); using var stream = new MemoryStream();
        PaaWriter.Write(stream, 16, 8, pixels, new PaaWriteOptions(PaaFormat.Dxt5));
        byte[] data = stream.ToArray();

        Assert.True(FindAscii(data, "GGATCGVA") >= 0); // AVGC
        Assert.True(FindAscii(data, "GGATCXAM") >= 0); // MAXC
        Assert.True(FindAscii(data, "GGATGALF") >= 0); // FLAG
        int offs = FindAscii(data, "GGATSFFO");
        Assert.True(offs >= 0);
        Assert.Equal(64, BitConverter.ToInt32(data, offs + 8));

        var offsets = Enumerable.Range(0, 16).Select(i => BitConverter.ToUInt32(data, offs + 12 + i * 4))
            .Where(value => value != 0).ToArray();
        Assert.NotEmpty(offsets);
        Assert.True(offsets.SequenceEqual(offsets.OrderBy(value => value)));
        Assert.All(offsets, offset => Assert.InRange(offset, 1u, (uint)data.Length - 1));
        Assert.Equal((ushort)16, BitConverter.ToUInt16(data, (int)offsets[0]));
        Assert.Equal((ushort)8, BitConverter.ToUInt16(data, (int)offsets[0] + 2));
    }

    [Fact] public void MipmapEncodingCanBeCancelled()
    {
        var pixels = Solid(64, 64, 10, 20, 30, 255); using var stream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => PaaWriter.Write(stream, 64, 64, pixels,
            new PaaWriteOptions(PaaFormat.Dxt1), cancellation.Token,
            progress => { if (progress > 0.1) cancellation.Cancel(); }));
    }

    private static int FindAscii(byte[] data, string value)
    {
        byte[] needle = System.Text.Encoding.ASCII.GetBytes(value);
        for (int i = 0; i <= data.Length - needle.Length; i++)
            if (data.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    private static byte[] Solid(int width, int height, byte b, byte g, byte r, byte a)
    {
        var result = new byte[width * height * 4];
        for (int i = 0; i < result.Length; i += 4) { result[i] = b; result[i + 1] = g; result[i + 2] = r; result[i + 3] = a; }
        return result;
    }
}
