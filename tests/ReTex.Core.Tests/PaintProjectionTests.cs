using ReTex.Core.Paint;

namespace ReTex.Core.Tests;

public sealed class PaintProjectionTests
{
    [Fact] public void BarycentricUvInterpolationNormalizesWeightsAndWrapsTiles()
    {
        float[][] uv = { new[] { -0.25f, 0.25f }, new[] { 0.75f, 0.25f }, new[] { 0.75f, 1.25f } };
        Assert.True(PaintProjection.TryInterpolateUv(uv, 0, 1, 2, 2, 1, 1, out var result));
        Assert.Equal(0.25, result.U, 6);
        Assert.Equal(0.5, result.V, 6);
    }

    [Fact] public void InvalidUvTriangleIsRejected()
    {
        float[][] uv = { new[] { 0f, 0f } };
        Assert.False(PaintProjection.TryInterpolateUv(uv, 0, 1, 2, 1, 0, 0, out _));
        Assert.False(PaintProjection.TryInterpolateUv(uv, 0, 0, 0, 0, 0, 0, out _));
    }

    [Fact] public void WrappedUvMapsInsideTextureBounds()
    {
        var texel = new PaintUv(-0.01, 2.999).ToTexel(1024, 512);
        Assert.InRange(texel.X, 0, 1023);
        Assert.InRange(texel.Y, 0, 511);
    }

    [Theory]
    [InlineData(-0.25, -0.25, 12, 6)]
    [InlineData(1.25, 1.25, 4, 2)]
    [InlineData(-1.75, 2.25, 4, 2)]
    public void MirroredAndTiledUvCoordinatesWrapToExpectedTexels(double u, double v, int x, int y)
    {
        var texel = new PaintUv(u, v).ToTexel(16, 8);

        Assert.Equal(x, (int)texel.X);
        Assert.Equal(y, (int)texel.Y);
    }

    [Fact] public void DiskSamplingAlwaysIncludesCenterAndNeverLeavesRadius()
    {
        PaintPoint[] points = PaintProjection.SampleDisk(20, 30, 10, 4).ToArray();
        Assert.Contains(new PaintPoint(20, 30), points);
        Assert.All(points, point => Assert.True(
            (point.X - 20) * (point.X - 20) + (point.Y - 30) * (point.Y - 30) <= 100.000001));
    }

    [Fact] public void ProjectedDiskCoalescesDuplicatesAcrossMultipleMaterials()
    {
        var projected = PaintProjection.ProjectDiskToTexels(10, 10, 8, 4, point =>
        {
            if (Math.Abs(point.X - 10) < 0.001 && Math.Abs(point.Y - 10) < 0.001)
                return new PaintProjectedTexel("body.paa", 5, 5);
            if (point.X < 10)
                return new PaintProjectedTexel("body.paa", 5, 5);
            return new PaintProjectedTexel("trim.paa", 2, 7);
        });

        Assert.Equal(2, projected.Count);
        Assert.Single(projected["body.paa"]);
        Assert.Single(projected["trim.paa"]);
        Assert.Contains(new PaintTexel(5, 5), projected["BODY.PAA"]);
        Assert.Contains(new PaintTexel(2, 7), projected["trim.paa"]);
    }
}
