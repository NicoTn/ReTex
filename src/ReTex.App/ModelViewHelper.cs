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
public static class ModelViewHelper
{
    /// <summary>
    /// <paramref name="loadTexture"/> loads the pixels for a resolved <see cref="PreviewTexture"/>
    /// (project .paa from disk or source .paa extracted from PBOs) - supplied by the caller so this
    /// stays free of the extraction/PAA pipeline. Returns null if the mesh has no usable geometry.
    /// </summary>
    public static Model3DGroup? Build(OdolLodMesh mesh, IReadOnlyList<OdolPreviewGroup> groups,
        Func<PreviewTexture, BitmapSource?> loadTexture)
    {
        if (mesh.Points.Length == 0 || groups.Count == 0) return null;

        // Shared vertex attributes (Arma X,Y,Z is Y-up, same as WPF).
        var positions = new Point3DCollection(mesh.Points.Length);
        foreach (var pt in mesh.Points) positions.Add(new Point3D(pt[0], pt[1], pt[2]));
        positions.Freeze();

        var texcoords = new PointCollection(mesh.Uv.Length);
        foreach (var uv in mesh.Uv) texcoords.Add(new System.Windows.Point(uv[0], uv[1]));
        texcoords.Freeze();

        var normals = new Vector3DCollection(mesh.Normals.Length);
        foreach (var n in mesh.Normals) normals.Add(new Vector3D(n[0], n[1], n[2]));
        normals.Freeze();

        var group = new Model3DGroup();
        // Self-contained lighting so the model is clearly visible regardless of the viewport's
        // headlight and regardless of normal orientation (ambient fill + two directional lights).
        group.Children.Add(new AmbientLight(Color.FromRgb(0x60, 0x60, 0x60)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0xC0, 0xC0, 0xC0), new Vector3D(-1, -1.5, -1)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0x70, 0x70, 0x70), new Vector3D(1, 1, 1)));

        foreach (var g in groups)
        {
            var indices = new Int32Collection(g.FaceIndices.Count * 6);
            foreach (int fi in g.FaceIndices)
            {
                var v = mesh.Faces[fi].VertexTableIndex;
                if (v.Length >= 3) { indices.Add(v[0]); indices.Add(v[1]); indices.Add(v[2]); }
                if (v.Length == 4) { indices.Add(v[0]); indices.Add(v[2]); indices.Add(v[3]); }
            }
            if (indices.Count == 0) continue;
            indices.Freeze();

            var geo = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            if (texcoords.Count == mesh.Points.Length) geo.TextureCoordinates = texcoords;
            if (normals.Count == mesh.Points.Length) geo.Normals = normals;
            geo.Freeze();

            Material material = BuildMaterial(g.Texture, loadTexture);
            var model = new GeometryModel3D(geo, material) { BackMaterial = material };
            model.Freeze();
            group.Children.Add(model);
        }

        return group.Children.Count == 0 ? null : Freeze(group);
    }

    private static Material BuildMaterial(PreviewTexture tex, Func<PreviewTexture, BitmapSource?> loadTexture)
    {
        BitmapSource? bmp = null;
        try { bmp = loadTexture(tex); } catch { /* fall through to a flat colour */ }
        if (bmp != null)
        {
            var brush = new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile };
            return new DiffuseMaterial(brush);
        }
        // No texture available: a neutral grey so geometry is still visible.
        var solid = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)));
        return solid;
    }

    private static Model3DGroup Freeze(Model3DGroup g) { g.Freeze(); return g; }
}
