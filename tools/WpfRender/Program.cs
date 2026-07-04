using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using ReTex.App;
using ReTex.Core.P3d;
using ReTex.Core.Paa;

namespace WpfRender;

// Headless WPF render of the app's actual preview Model3DGroup (built by ModelViewHelper), so the
// 3D-preview texture mapping can be verified through the real WPF pipeline without launching the UI.
// Usage: WpfRender <p3d> <paa> <out.png>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--window") return RenderWindow(args[1]);
        // Real per-selection retexture pipeline (exactly what the app does): apply a distinct .paa to
        // each named selection and render. WpfRender <p3d> --retex <out.png> sel=paa [sel=paa ...]
        if (args.Length >= 4 && args[1] == "--retex") return RenderRetex(args);
        if (args.Length < 3) { Console.WriteLine("usage: WpfRender <p3d> <paa|--uvgrid> <out.png>  |  <p3d> --retex <out.png> sel=paa...  |  --window <out.png>"); return 2; }

        var mesh = OdolLodReader.ReadAnyVisualLod(File.ReadAllBytes(args[0]));
        if (mesh is null) { Console.WriteLine("mesh decode returned null"); return 1; }

        // A synthetic UV grid (arg == "--uvgrid") makes UV alignment obvious: distinct per-cell colours
        // + a checkerboard reveal stretching/offset that a dark photographic texture can hide.
        BitmapSource bmp;
        int texW, texH;
        if (string.Equals(args[1], "--uvgrid", StringComparison.OrdinalIgnoreCase))
        {
            const int G = 512, cells = 8, cs = G / cells;
            var px = new byte[G * G * 4];
            for (int y = 0; y < G; y++)
                for (int x = 0; x < G; x++)
                {
                    int cx = x / cs, cy = y / cs;
                    bool alt = ((cx + cy) & 1) == 0;
                    byte r = (byte)(cx * 255 / (cells - 1)), g = (byte)(cy * 255 / (cells - 1));
                    int o = (y * G + x) * 4;
                    px[o + 0] = (byte)(alt ? 40 : 255 - g); // B
                    px[o + 1] = (byte)(alt ? g : 40);       // G
                    px[o + 2] = (byte)(alt ? r : 255 - r);  // R
                    px[o + 3] = 255;                        // A
                }
            var grid = BitmapSource.Create(G, G, 96, 96, PixelFormats.Bgra32, null, px, G * 4);
            grid.Freeze();
            bmp = grid; texW = texH = G;
        }
        else
        {
            var paa = PaaImage.LoadFile(args[1]);
            bmp = BitmapSource.Create(paa.Width, paa.Height, 96, 96, PixelFormats.Bgra32, null,
                paa.Bgra, paa.Width * 4);
            bmp.Freeze();
            texW = paa.Width; texH = paa.Height;
        }

        var groups = OdolMeshPreview.BuildGroups(mesh, null, null);
        var built = ModelViewHelper.Build(mesh, groups, _ => bmp);
        if (built is null) { Console.WriteLine("ModelViewHelper.Build returned null"); return 1; }
        Model3D model = built.Model;

        // Frame the model with a perspective camera on +Z looking toward -Z (same view as the
        // software render's axis=z), so the two can be compared directly.
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in mesh.Points)
        {
            minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]);
            minY = Math.Min(minY, p[1]); maxY = Math.Max(maxY, p[1]);
            minZ = Math.Min(minZ, p[2]); maxZ = Math.Max(maxZ, p[2]);
        }
        var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        double size = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        double fov = 45;
        double dist = size / (2 * Math.Tan(fov * Math.PI / 360)) * 1.6;

        var camera = new PerspectiveCamera(
            new Point3D(center.X, center.Y, center.Z + dist),
            new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), fov);

        var visual = new ModelVisual3D { Content = model };
        var viewport = new Viewport3D { Camera = camera };
        viewport.Children.Add(visual);

        const int W = 700, H = 700;
        viewport.Width = W; viewport.Height = H;
        viewport.Measure(new Size(W, H));
        viewport.Arrange(new Rect(0, 0, W, H));
        viewport.UpdateLayout();

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(viewport);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(args[2]);
        enc.Save(fs);
        Console.WriteLine($"Rendered {mesh.Points.Length} pts / {mesh.Faces.Count} faces, tex {texW}x{texH} -> {args[2]}");
        return 0;
    }

    /// <summary>Applies a distinct .paa per named selection (the real BuildGroups-with-selections +
    /// ModelViewHelper.Build path) and renders, so the retexture's on-model alignment can be checked
    /// against ground truth. Args: &lt;p3d&gt; --retex &lt;out.png&gt; sel=paa [sel=paa ...]</summary>
    private static int RenderRetex(string[] args)
    {
        var mesh = OdolLodReader.ReadAnyVisualLod(File.ReadAllBytes(args[0]));
        if (mesh is null) { Console.WriteLine("mesh decode returned null"); return 1; }
        string outPng = args[2];

        var sels = new List<ReTex.Core.Projects.RetexSelection>();
        var bitmaps = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in args.Skip(3))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            string sel = pair[..eq], paaPath = pair[(eq + 1)..];
            // projectAddonDir=null -> ProjectFilePath == ProjectTexture == the absolute .paa path.
            sels.Add(new ReTex.Core.Projects.RetexSelection { Name = sel, ProjectTexture = paaPath });
            var paa = PaaImage.LoadFile(paaPath);
            var b = BitmapSource.Create(paa.Width, paa.Height, 96, 96, PixelFormats.Bgra32, null, paa.Bgra, paa.Width * 4);
            b.Freeze();
            bitmaps[paaPath] = b;
            Console.WriteLine($"  selection '{sel}' <- {Path.GetFileName(paaPath)} ({paa.Width}x{paa.Height})");
        }

        var groups = OdolMeshPreview.BuildGroups(mesh, sels, null);
        var grey = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 128, 128, 128, 255 }, 4);
        grey.Freeze();
        var built = ModelViewHelper.Build(mesh, groups, tex =>
            tex.ProjectFilePath != null && bitmaps.TryGetValue(tex.ProjectFilePath, out var b) ? b : grey);
        if (built is null) { Console.WriteLine("ModelViewHelper.Build returned null"); return 1; }

        foreach (var g in groups)
            Console.WriteLine($"  group {g.FaceIndices.Count} faces retex={g.Texture.IsRetextured} src={g.Texture.SourceVirtualPath}");
        SaveModelPng(mesh, built.Model, outPng, 900, 900);
        Console.WriteLine($"Rendered retex -> {outPng}");
        return 0;
    }

    /// <summary>Frames the model on +Z and writes a PNG (shared by the render modes).</summary>
    private static void SaveModelPng(OdolLodMesh mesh, Model3D model, string outPng, int w, int h)
    {
        // Frame from vertices actually referenced by faces (stray proxy/unused points would otherwise
        // inflate the bbox and shrink the model to a dot).
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var f in mesh.Faces)
            foreach (var vi in f.VertexTableIndex)
            {
                if (vi < 0 || vi >= mesh.Points.Length) continue;
                var p = mesh.Points[vi];
                minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]);
                minY = Math.Min(minY, p[1]); maxY = Math.Max(maxY, p[1]);
                minZ = Math.Min(minZ, p[2]); maxZ = Math.Max(maxZ, p[2]);
            }
        var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        double size = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        double zoom = double.TryParse(Environment.GetEnvironmentVariable("WR_ZOOM"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z) ? z : 1.2;
        double fov = 45, dist = size / (2 * Math.Tan(fov * Math.PI / 360)) * zoom;
        var camera = new PerspectiveCamera(new Point3D(center.X, center.Y, center.Z + dist),
            new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), fov);
        var viewport = new Viewport3D { Camera = camera };
        var mv = new ModelVisual3D { Content = model };
        var flip = Environment.GetEnvironmentVariable("WR_FLIP") ?? "";
        if (flip.Length > 0)
            mv.Transform = new ScaleTransform3D(
                flip.Contains('x') ? -1 : 1, flip.Contains('y') ? -1 : 1, flip.Contains('z') ? -1 : 1,
                center.X, center.Y, center.Z);
        viewport.Children.Add(mv);
        viewport.Width = w; viewport.Height = h;
        viewport.Measure(new Size(w, h));
        viewport.Arrange(new Rect(0, 0, w, h));
        viewport.UpdateLayout();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(viewport);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outPng);
        enc.Save(fs);
    }

    /// <summary>Renders the app's MainWindow (real layout, real bindings) to a PNG so the UI
    /// arrangement can be verified without a display. Shown off-screen so layout resolves.</summary>
    private static int RenderWindow(string outPng)
    {
        const int W = 1400, H = 860;
        var win = new ReTex.App.MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowInTaskbar = false,
            Width = W, Height = H,
        };
        win.Show();
        win.UpdateLayout();
        // Let async bindings / measure settle.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

        var root = (FrameworkElement)win.Content;
        int rw = (int)Math.Ceiling(root.ActualWidth), rh = (int)Math.Ceiling(root.ActualHeight);
        if (rw <= 0 || rh <= 0) { rw = W; rh = H; }
        var rtb = new RenderTargetBitmap(rw, rh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPng)) enc.Save(fs);
        win.Close();
        Console.WriteLine($"Rendered MainWindow {rw}x{rh} -> {outPng}");
        return 0;
    }
}
