namespace ReTex.Core.Paint;

public readonly record struct PaintUv(double U, double V)
{
    public PaintUv Wrapped() => new(Wrap(U), Wrap(V));

    public PaintPoint ToTexel(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var wrapped = Wrapped();
        return new PaintPoint(Math.Min(width - 1, wrapped.U * width),
            Math.Min(height - 1, wrapped.V * height));
    }

    private static double Wrap(double value) => value - Math.Floor(value);
}

public readonly record struct PaintTexel(int X, int Y);
public readonly record struct PaintProjectedTexel(string TextureKey, int X, int Y);

public static class PaintProjection
{
    public static bool TryInterpolateUv(IReadOnlyList<float[]> uv, int index1, int index2, int index3,
        double weight1, double weight2, double weight3, out PaintUv result)
    {
        result = default;
        if ((uint)index1 >= (uint)uv.Count || (uint)index2 >= (uint)uv.Count || (uint)index3 >= (uint)uv.Count)
            return false;
        if (uv[index1].Length < 2 || uv[index2].Length < 2 || uv[index3].Length < 2)
            return false;
        double total = weight1 + weight2 + weight3;
        if (!double.IsFinite(total) || Math.Abs(total) < 1e-12) return false;
        weight1 /= total; weight2 /= total; weight3 /= total;
        double u = weight1 * uv[index1][0] + weight2 * uv[index2][0] + weight3 * uv[index3][0];
        double v = weight1 * uv[index1][1] + weight2 * uv[index2][1] + weight3 * uv[index3][1];
        if (!double.IsFinite(u) || !double.IsFinite(v)) return false;
        result = new PaintUv(u, v).Wrapped();
        return true;
    }

    public static IEnumerable<PaintPoint> SampleDisk(double centerX, double centerY, double radius,
        double spacing)
    {
        radius = Math.Max(0, radius); spacing = Math.Max(0.25, spacing);
        yield return new PaintPoint(centerX, centerY);
        if (radius <= 0) yield break;
        double radiusSquared = radius * radius;
        for (double y = -radius; y <= radius + 1e-9; y += spacing)
        for (double x = -radius; x <= radius + 1e-9; x += spacing)
        {
            if (x * x + y * y > radiusSquared || Math.Abs(x) < 1e-9 && Math.Abs(y) < 1e-9) continue;
            yield return new PaintPoint(centerX + x, centerY + y);
        }
    }

    public static IReadOnlyDictionary<string, IReadOnlyCollection<PaintTexel>> ProjectDiskToTexels(
        double centerX, double centerY, double radius, double spacing,
        Func<PaintPoint, PaintProjectedTexel?> hitTest)
    {
        var hits = new Dictionary<string, HashSet<PaintTexel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in SampleDisk(centerX, centerY, radius, spacing))
        {
            var hit = hitTest(sample);
            if (hit is not { } projected || string.IsNullOrWhiteSpace(projected.TextureKey)) continue;
            if (!hits.TryGetValue(projected.TextureKey, out var texels))
                hits[projected.TextureKey] = texels = new HashSet<PaintTexel>();
            texels.Add(new PaintTexel(projected.X, projected.Y));
        }

        return hits.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<PaintTexel>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }
}
