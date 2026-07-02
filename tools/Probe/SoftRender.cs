using ReTex.Core.P3d;
using ReTex.Core.Paa;

namespace Probe;

/// <summary>Headless orthographic textured rasterizer for an <see cref="OdolLodMesh"/> - lets the
/// 3D-preview UV/geometry decode be verified visually without launching WPF. Samples the given
/// texture with the mesh's own UVs (no WPF ImageBrush), so a correct render here means the Core
/// decode is right and any remaining misalignment is WPF-specific.</summary>
public static class SoftRender
{
    /// <param name="axis">View axis: 'z' looks down -Z (screen X=modelX, Y=modelY, Y-up),
    /// 'x' looks down -X (screen X=modelZ, Y=modelY), 'y' looks down -Y/top (screen X=modelX, Y=modelZ).</param>
    /// <param name="flipV">Sample the texture with V flipped (1-v).</param>
    public static byte[] Render(OdolLodMesh mesh, PaaImage tex, int W, int H, char axis, bool flipV)
    {
        var img = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { img[i * 4] = 30; img[i * 4 + 1] = 30; img[i * 4 + 2] = 34; img[i * 4 + 3] = 255; }
        var zbuf = new float[W * H];
        Array.Fill(zbuf, float.NegativeInfinity);

        // Model bbox for the two on-screen axes + normalisation.
        float minH = float.MaxValue, maxH = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        (float h, float v, float d) Map(float[] p) => axis switch
        {
            'x' => (p[2], p[1], p[0]),
            'y' => (p[0], p[2], p[1]),
            _ => (p[0], p[1], p[2]),
        };
        foreach (var p in mesh.Points)
        {
            var (h, v, _) = Map(p);
            if (h < minH) minH = h; if (h > maxH) maxH = h;
            if (v < minV) minV = v; if (v > maxV) maxV = v;
        }
        float spanH = Math.Max(maxH - minH, 1e-6f), spanV = Math.Max(maxV - minV, 1e-6f);
        float span = Math.Max(spanH, spanV);
        float pad = 0.06f * span;
        float sc = (Math.Min(W, H) - 1) / (span + 2 * pad);
        float cx = (minH + maxH) / 2, cy = (minV + maxV) / 2;

        (float sx, float sy, float d, float u, float w) Project(int vi)
        {
            var (h, v, d) = Map(mesh.Points[vi]);
            float sx = W / 2f + (h - cx) * sc;
            float sy = H / 2f - (v - cy) * sc;               // Y-up -> image top-down
            var uv = vi < mesh.Uv.Length && mesh.Uv[vi].Length == 2 ? mesh.Uv[vi] : new float[] { 0, 0 };
            return (sx, sy, d, uv[0], uv[1]);
        }

        foreach (var f in mesh.Faces)
        {
            var idx = f.VertexTableIndex;
            for (int t = 0; t + 2 < idx.Length; t++)               // tri fan: (0,1,2),(0,2,3)
                Tri(Project(idx[0]), Project(idx[t + 1]), Project(idx[t + 2]));
        }

        void Tri((float sx, float sy, float d, float u, float w) a,
                 (float sx, float sy, float d, float u, float w) b,
                 (float sx, float sy, float d, float u, float w) c)
        {
            int minX = (int)MathF.Floor(Math.Min(a.sx, Math.Min(b.sx, c.sx)));
            int maxX = (int)MathF.Ceiling(Math.Max(a.sx, Math.Max(b.sx, c.sx)));
            int minY = (int)MathF.Floor(Math.Min(a.sy, Math.Min(b.sy, c.sy)));
            int maxY = (int)MathF.Ceiling(Math.Max(a.sy, Math.Max(b.sy, c.sy)));
            minX = Math.Max(minX, 0); maxX = Math.Min(maxX, W - 1);
            minY = Math.Max(minY, 0); maxY = Math.Min(maxY, H - 1);
            float area = Edge(a, b, c);
            if (MathF.Abs(area) < 1e-9f) return;
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = (sx: x + 0.5f, sy: y + 0.5f, d: 0f, u: 0f, w: 0f);
                float w0 = Edge(b, c, p), w1 = Edge(c, a, p), w2 = Edge(a, b, p);
                if ((w0 < 0 || w1 < 0 || w2 < 0) && (w0 > 0 || w1 > 0 || w2 > 0)) continue; // outside
                w0 /= area; w1 /= area; w2 /= area;
                float depth = w0 * a.d + w1 * b.d + w2 * c.d;
                int zi = y * W + x;
                if (depth <= zbuf[zi]) continue;                  // keep nearest-to-viewer (larger axis value)
                zbuf[zi] = depth;
                float u = w0 * a.u + w1 * b.u + w2 * c.u;
                float v = w0 * a.w + w1 * b.w + w2 * c.w;
                Sample(u, v, out byte r, out byte g, out byte bl);
                img[zi * 4] = r; img[zi * 4 + 1] = g; img[zi * 4 + 2] = bl; img[zi * 4 + 3] = 255;
            }
        }

        float Edge((float sx, float sy, float d, float u, float w) a,
                   (float sx, float sy, float d, float u, float w) b,
                   (float sx, float sy, float d, float u, float w) c)
            => (c.sx - a.sx) * (b.sy - a.sy) - (c.sy - a.sy) * (b.sx - a.sx);

        void Sample(float u, float v, out byte r, out byte g, out byte b)
        {
            if (flipV) v = 1 - v;
            u -= MathF.Floor(u); v -= MathF.Floor(v);             // wrap (TileMode.Tile)
            int tx = Math.Clamp((int)(u * tex.Width), 0, tex.Width - 1);
            int ty = Math.Clamp((int)(v * tex.Height), 0, tex.Height - 1);
            int k = (ty * tex.Width + tx) * 4;
            b = tex.Bgra[k]; g = tex.Bgra[k + 1]; r = tex.Bgra[k + 2];
        }

        return img;
    }
}
