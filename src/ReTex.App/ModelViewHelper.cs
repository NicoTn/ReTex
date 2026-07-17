using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using ReTex.Core.P3d;

namespace ReTex.App;

/// <summary>Builds a WPF <see cref="Model3DGroup"/> from an <see cref="OdolLodMesh"/> and its
/// resolved texture groups (Phase 2). Pure WPF Media3D - HelixToolkit is only used for the
/// viewport control in XAML. One <see cref="GeometryModel3D"/> per texture group, textured with a
/// diffuse <see cref="ImageBrush"/> (front + back material so winding never hides a face in the
/// preview). Mirrors <see cref="ImageHelper"/>'s pattern of freezing the result for the UI thread.</summary>
/// <summary>The built model plus a map from each rendered part (<see cref="GeometryModel3D"/>) to
/// the texture group it came from, so a hover hit-test can name the texture under the cursor.</summary>
public sealed class BuiltPreview
{
    public required Model3DGroup Model { get; init; }
    public required IReadOnlyDictionary<GeometryModel3D, OdolPreviewGroup> Parts { get; init; }
    /// <summary>Brushes keyed by absolute project PAA path. They are mutable on the UI-owned result
    /// returned by <see cref="ModelViewHelper.ActivateLiveTextures"/>; updating ImageSource then changes
    /// the rendered texture without rebuilding the model or geometry.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<ImageBrush>> ProjectTextureBrushes { get; init; }
    /// <summary>Transparent diffuse layers keyed by project PAA path. Paint Studio updates these with
    /// selection masks so selected UV regions are visible on the model without changing texture pixels.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<ImageBrush>> SelectionOverlayBrushes { get; init; }
}

/// <summary>A debug/experimentation transform applied to every texture coordinate before the preview
/// is built, so the texture can be flipped, rotated (90° steps) and shifted live to find the mapping
/// that matches the in-game look. <see cref="Identity"/> is the shipping default (no change).</summary>
public readonly record struct UvXform(bool FlipU, bool FlipV, int Rot90, double OffsetU, double OffsetV)
{
    public static readonly UvXform Identity = new(false, false, 0, 0, 0);
    public bool IsIdentity => this == Identity;

    /// <summary>Apply to a single (u,v): rotate about the texture centre, then flip, then offset.</summary>
    public System.Windows.Point Apply(double u, double v)
    {
        int k = ((Rot90 % 4) + 4) % 4;
        for (int i = 0; i < k; i++) { double nu = 0.5 - (v - 0.5), nv = 0.5 + (u - 0.5); u = nu; v = nv; } // 90° CCW
        if (FlipU) u = 1.0 - u;
        if (FlipV) v = 1.0 - v;
        return new System.Windows.Point(u + OffsetU, v + OffsetV);
    }
}

public static class ModelViewHelper
{
    /// <summary>A single shared, frozen specular layer added under every textured part (semi-matte
    /// sheen tuned for Arma gear). Reused so we don't allocate one per part.</summary>
    private static readonly SpecularMaterial SharedSpecular = CreateSharedSpecular();
    private static SpecularMaterial CreateSharedSpecular()
    {
        var m = new SpecularMaterial(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 16.0);
        m.Freeze();
        return m;
    }

    /// <summary>Builds a self-identifying UV coordinate grid as a frozen bitmap: a <paramref name="cells"/>
    /// × <paramref name="cells"/> grid where each cell is a distinct pastel colour and prints its centre
    /// UV (e.g. "0.35,0.45"), with U→right / V↓down axis arrows. Wearing this on the model (and viewing
    /// the same grid in-engine) makes texture placement directly comparable region-by-region. Must run
    /// on the UI (STA) thread. </summary>
    public static BitmapSource BuildCoordinateGrid(int size = 1024, int cells = 10)
    {
        var typeface = new Typeface("Segoe UI");
        double cs = (double)size / cells;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    // Light pastel so black labels stay readable; hue varies with cell so colour alone
                    // distinguishes regions even at a glance.
                    byte r = (byte)(120 + cx * (135.0 / (cells - 1)));
                    byte g = (byte)(120 + cy * (135.0 / (cells - 1)));
                    var fill = new SolidColorBrush(Color.FromRgb(r, g, 190));
                    var rect = new Rect(cx * cs, cy * cs, cs, cs);
                    dc.DrawRectangle(fill, null, rect);
                    double u = (cx + 0.5) / cells, v = (cy + 0.5) / cells;
                    var ft = new FormattedText($"{u:0.00},{v:0.00}", CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, Math.Max(9, cs * 0.16), Brushes.Black, 1.0);
                    dc.DrawText(ft, new Point(cx * cs + 4, cy * cs + cs / 2 - ft.Height / 2));
                }
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 1);
            pen.Freeze();
            for (int i = 0; i <= cells; i++)
            {
                dc.DrawLine(pen, new Point(i * cs, 0), new Point(i * cs, size));
                dc.DrawLine(pen, new Point(0, i * cs), new Point(size, i * cs));
            }
            // Axis arrows: U increases rightward (top edge), V increases downward (left edge).
            var axis = new FormattedText("U →", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.05, Brushes.DarkRed, 1.0);
            dc.DrawText(axis, new Point(size * 0.5, 2));
            var axisV = new FormattedText("V ↓", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.05, Brushes.DarkBlue, 1.0);
            dc.DrawText(axisV, new Point(2, size * 0.5));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// <paramref name="loadTexture"/> loads the pixels for a resolved <see cref="PreviewTexture"/>
    /// (project .paa from disk or source .paa extracted from PBOs) - supplied by the caller so this
    /// stays free of the extraction/PAA pipeline. Returns null if the mesh has no usable geometry.
    /// </summary>
    public static BuiltPreview? Build(OdolLodMesh mesh, IReadOnlyList<OdolPreviewGroup> groups,
        Func<PreviewTexture, BitmapSource?> loadTexture, UvXform uv = default, bool renderBackFaces = true,
        bool reverseWinding = false)
    {
        if (mesh.Points.Length == 0 || groups.Count == 0) return null;

        // Arma uses a left-handed coordinate system (X right, Y up, Z forward); WPF Media3D is
        // right-handed. Negate forward/Z, not X: either axis changes handedness, but negating X mirrors
        // asymmetric markings onto the opposite arm/knee. Z conversion preserves in-game left/right
        // and faces the model toward Helix's default camera.
        var positions = new Point3DCollection(mesh.Points.Length);
        foreach (var pt in mesh.Points) positions.Add(new Point3D(pt[0], pt[1], -pt[2]));
        positions.Freeze();

        // The optional UvXform is applied uniformly so the preview and the UV-debug tools stay in
        // lock-step when we need to compensate for a model/renderer texture-origin mismatch.
        var texcoords = new PointCollection(mesh.Uv.Length);
        if (uv.IsIdentity)
            foreach (var t in mesh.Uv) texcoords.Add(new System.Windows.Point(t[0], t[1]));
        else
            foreach (var t in mesh.Uv) texcoords.Add(uv.Apply(t[0], t[1]));
        texcoords.Freeze();

        var normals = new Vector3DCollection(mesh.Normals.Length);
        foreach (var n in mesh.Normals) normals.Add(new Vector3D(n[0], n[1], -n[2]));
        normals.Freeze();

        var group = new Model3DGroup();
        var parts = new Dictionary<GeometryModel3D, OdolPreviewGroup>();
        var projectBrushes = new Dictionary<string, List<ImageBrush>>(StringComparer.OrdinalIgnoreCase);
        // Self-contained three-point-ish rig. Ambient kept moderate (dark Arma armour must still read
        // against the dark viewport) but lower than before so the specular highlights + form show; a
        // strong key from the upper front, a soft fill from the opposite side, and a rim/back light to
        // pop the silhouette. The viewport's headlight adds view-tracking specular on top in the app.
        group.Children.Add(new AmbientLight(Color.FromRgb(0x63, 0x63, 0x63)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0xD2, 0xD2, 0xD2), new Vector3D(-0.5, -1, -1)));   // key (front-upper-left)
        group.Children.Add(new DirectionalLight(Color.FromRgb(0x5A, 0x5A, 0x5A), new Vector3D(1, 0.3, 0.8)));    // fill (lower-right, from front)
        group.Children.Add(new DirectionalLight(Color.FromRgb(0x4B, 0x4B, 0x4B), new Vector3D(0.3, -0.6, 1)));   // rim/back (behind-above)

        foreach (var g in groups)
        {
            var indices = new Int32Collection(g.FaceIndices.Count * 6);
            foreach (int fi in g.FaceIndices)
            {
                var v = mesh.Faces[fi].VertexTableIndex;
                if (!reverseWinding)
                {
                    if (v.Length >= 3) { indices.Add(v[0]); indices.Add(v[1]); indices.Add(v[2]); }
                    if (v.Length == 4) { indices.Add(v[0]); indices.Add(v[2]); indices.Add(v[3]); }
                }
                else
                {
                    if (v.Length >= 3) { indices.Add(v[0]); indices.Add(v[2]); indices.Add(v[1]); }
                    if (v.Length == 4) { indices.Add(v[0]); indices.Add(v[3]); indices.Add(v[2]); }
                }
            }
            if (indices.Count == 0) continue;
            indices.Freeze();

            var geo = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            if (texcoords.Count == mesh.Points.Length) geo.TextureCoordinates = texcoords;
            if (normals.Count == mesh.Points.Length) geo.Normals = normals;
            geo.Freeze();

            var (material, liveBrush, livePath) = BuildMaterial(g.Texture, loadTexture);
            var model = new GeometryModel3D(geo, material)
            {
                BackMaterial = renderBackFaces ? material : null,
            };
            model.Freeze();
            if (liveBrush is not null && livePath is not null)
            {
                if (!projectBrushes.TryGetValue(livePath, out var brushes))
                    projectBrushes[livePath] = brushes = new List<ImageBrush>();
                brushes.Add(liveBrush);
            }
            group.Children.Add(model);
            parts[model] = g;
        }

        if (parts.Count == 0) return null;
        return new BuiltPreview
        {
            Model = Freeze(group),
            Parts = parts,
            ProjectTextureBrushes = projectBrushes.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ImageBrush>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            SelectionOverlayBrushes = new Dictionary<string, IReadOnlyList<ImageBrush>>(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Creates a UI-thread-owned model that reuses the frozen geometry and lights from a
    /// background-built preview, but clones project materials so their ImageBrush can be replaced in
    /// place when a watched PAA changes.</summary>
    public static BuiltPreview ActivateLiveTextures(Model3DGroup frozenModel,
        IReadOnlyList<OdolPreviewGroup> groups)
    {
        var result = new Model3DGroup();
        var parts = new Dictionary<GeometryModel3D, OdolPreviewGroup>();
        var brushesByPath = new Dictionary<string, List<ImageBrush>>(StringComparer.OrdinalIgnoreCase);
        var selectionBrushesByPath = new Dictionary<string, List<ImageBrush>>(StringComparer.OrdinalIgnoreCase);
        int groupIndex = 0;

        foreach (var child in frozenModel.Children)
        {
            if (child is not GeometryModel3D sourceModel || groupIndex >= groups.Count)
            {
                result.Children.Add(child); // frozen lights are safe to share
                continue;
            }

            var previewGroup = groups[groupIndex++];
            string? projectPath = NormalizeProjectPath(previewGroup.Texture.ProjectFilePath);
            if (projectPath is null)
            {
                result.Children.Add(sourceModel);
                parts[sourceModel] = previewGroup;
                continue;
            }

            var material = sourceModel.Material?.CloneCurrentValue();
            if (material is null)
            {
                result.Children.Add(sourceModel);
                parts[sourceModel] = previewGroup;
                continue;
            }
            var brush = FindImageBrush(material);
            (material, ImageBrush selectionBrush) = AddSelectionOverlay(material);
            var liveModel = new GeometryModel3D(sourceModel.Geometry, material)
            {
                BackMaterial = sourceModel.BackMaterial is null ? null : material,
            };
            result.Children.Add(liveModel);
            parts[liveModel] = previewGroup;
            if (brush is not null)
            {
                if (!brushesByPath.TryGetValue(projectPath, out var list))
                    brushesByPath[projectPath] = list = new List<ImageBrush>();
                list.Add(brush);
            }
            if (!selectionBrushesByPath.TryGetValue(projectPath, out var selectionList))
                selectionBrushesByPath[projectPath] = selectionList = new List<ImageBrush>();
            selectionList.Add(selectionBrush);
        }

        return new BuiltPreview
        {
            Model = result,
            Parts = parts,
            ProjectTextureBrushes = brushesByPath.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ImageBrush>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            SelectionOverlayBrushes = selectionBrushesByPath.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ImageBrush>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private static (Material Material, ImageBrush Brush) AddSelectionOverlay(Material material)
    {
        var brush = new ImageBrush(TransparentPixel)
        {
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            TileMode = TileMode.Tile,
        };
        MaterialGroup group;
        if (material is MaterialGroup existing && !existing.IsFrozen)
        {
            group = existing;
        }
        else
        {
            group = new MaterialGroup();
            group.Children.Add(material);
        }
        group.Children.Add(new DiffuseMaterial(brush));
        return (group, brush);
    }

    private static ImageBrush? FindImageBrush(Material material)
    {
        if (material is DiffuseMaterial { Brush: ImageBrush brush }) return brush;
        if (material is MaterialGroup group)
            foreach (var child in group.Children)
                if (FindImageBrush(child) is { } found) return found;
        return null;
    }

    private static readonly BitmapSource TransparentPixel = CreateTransparentPixel();

    private static BitmapSource CreateTransparentPixel()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 0, 0 }, 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static string? NormalizeProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static (Material Material, ImageBrush? LiveBrush, string? LivePath) BuildMaterial(
        PreviewTexture tex, Func<PreviewTexture, BitmapSource?> loadTexture)
    {
        BitmapSource? bmp = null;
        try { bmp = loadTexture(tex); } catch { /* fall through to a flat colour */ }
        string? projectPath = null;
        if (!string.IsNullOrWhiteSpace(tex.ProjectFilePath))
        {
            try { projectPath = Path.GetFullPath(tex.ProjectFilePath); }
            catch { projectPath = tex.ProjectFilePath; }
        }
        if (bmp != null || projectPath is not null)
        {
            var brush = new ImageBrush(bmp ?? MissingTexture)
            {
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                TileMode = TileMode.Tile
            };
            // Diffuse (the retexture) + a subtle broad specular so armour plates catch the light and read
            // as three-dimensional instead of flat. Media3D can't sample a spec MAP, so this is a uniform
            // sheen tuned to Arma gear (semi-matte); the light rig above supplies the highlights.
            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(brush));
            group.Children.Add(SharedSpecular);
            if (projectPath is null) group.Freeze();
            return (group, projectPath is null ? null : brush, projectPath);
        }
        // No texture available: a neutral grey so geometry is still visible.
        var solid = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)));
        solid.Freeze();
        return (solid, null, null);
    }

    private static readonly BitmapSource MissingTexture = CreateMissingTexture();
    internal static BitmapSource MissingTextureBitmap => MissingTexture;
    private static BitmapSource CreateMissingTexture()
    {
        var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0xB0, 0xB0, 0xB0, 0xFF }, 4);
        bmp.Freeze();
        return bmp;
    }

    private static Model3DGroup Freeze(Model3DGroup g) { g.Freeze(); return g; }

    /// <summary>Builds a bright glowing overlay of every face whose UV centroid is within
    /// <paramref name="radius"/> of (<paramref name="u"/>,<paramref name="v"/>) — i.e. the geometry
    /// that samples the clicked spot on the texture. Positions match the main preview (negated X, nudged
    /// along the normal to sit on top). Returns null if nothing is near. UI (STA) thread.</summary>
    public static Model3DGroup? BuildUvHighlight(OdolLodMesh mesh, double u, double v, double radius, UvXform uv = default)
    {
        if (mesh.Points.Length == 0 || mesh.Uv.Length == 0) return null;
        const double eps = 0.006; // normal nudge so the overlay wins the depth test over the base skin
        var pts = new Point3DCollection();
        var idx = new Int32Collection();
        var local = new Dictionary<int, int>();
        int Local(int vi)
        {
            if (local.TryGetValue(vi, out var li)) return li;
            var p = mesh.Points[vi];
            var n = mesh.Normals.Length > vi ? mesh.Normals[vi] : new[] { 0f, 0f, 0f };
            li = pts.Count;
            pts.Add(new Point3D(p[0] + n[0] * eps, p[1] + n[1] * eps, -p[2] + -n[2] * eps));
            local[vi] = li;
            return li;
        }
        foreach (var f in mesh.Faces)
        {
            var vtx = f.VertexTableIndex;
            if (vtx.Any(i => i >= mesh.Uv.Length)) continue;
            double cu = 0, cv = 0;
            foreach (var i in vtx)
            {
                var t = mesh.Uv[i];
                var p = uv.Apply(t[0], t[1]);
                cu += p.X;
                cv += p.Y;
            }
            cu /= vtx.Length; cv /= vtx.Length;
            // wrap into the same [0,1) space the pick uses so tiled UVs still match
            cu -= Math.Floor(cu); cv -= Math.Floor(cv);
            if (Math.Abs(cu - u) > radius || Math.Abs(cv - v) > radius) continue;
            if (vtx.Length >= 3) { idx.Add(Local(vtx[0])); idx.Add(Local(vtx[1])); idx.Add(Local(vtx[2])); }
            if (vtx.Length == 4) { idx.Add(Local(vtx[0])); idx.Add(Local(vtx[2])); idx.Add(Local(vtx[3])); }
        }
        if (idx.Count == 0) return null;
        pts.Freeze(); idx.Freeze();
        var geo = new MeshGeometry3D { Positions = pts, TriangleIndices = idx };
        geo.Freeze();
        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0xC0, 0x00, 0xE5, 0xFF))));
        mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0x00, 0xB8, 0xD4))));
        mat.Freeze();
        var model = new GeometryModel3D(geo, mat) { BackMaterial = mat };
        model.Freeze();
        var group = new Model3DGroup();
        group.Children.Add(model);
        return Freeze(group);
    }

    /// <summary>Renders the model's UV layout (wireframe of every face's UV edges) to a transparent
    /// bitmap of the given square size, one colour per texture group — a paint-alignment guide the
    /// user can drop over their texture as a layer. Must run on the UI (STA) thread.</summary>
    public static BitmapSource RenderUvMap(OdolLodMesh mesh, IReadOnlyList<OdolPreviewGroup> groups, int size, UvXform uv = default)
    {
        // Distinct, high-contrast colours per group so e.g. the "camo" body and a secondary selection
        // read apart. Cycled if there are more groups than colours.
        Color[] palette =
        {
            Color.FromRgb(0x33, 0xFF, 0x66), Color.FromRgb(0xFF, 0x55, 0x55),
            Color.FromRgb(0x55, 0xAA, 0xFF), Color.FromRgb(0xFF, 0xCC, 0x33),
            Color.FromRgb(0xCC, 0x66, 0xFF), Color.FromRgb(0x33, 0xFF, 0xFF),
        };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            System.Windows.Point P(int vi)
            {
                var t = mesh.Uv[vi];
                var p = uv.Apply(t[0], t[1]);
                return new System.Windows.Point(p.X * size, p.Y * size);
            }
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var pen = new System.Windows.Media.Pen(new SolidColorBrush(palette[gi % palette.Length]), Math.Max(0.5, size / 2048.0));
                pen.Freeze();
                foreach (int fi in groups[gi].FaceIndices)
                {
                    if (fi < 0 || fi >= mesh.Faces.Count) continue;
                    var v = mesh.Faces[fi].VertexTableIndex;
                    if (v.Any(i => i >= mesh.Uv.Length)) continue;
                    for (int i = 0; i < v.Length; i++)
                        dc.DrawLine(pen, P(v[i]), P(v[(i + 1) % v.Length]));
                }
            }
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
