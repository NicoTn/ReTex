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
        if (args.Length < 3) { Console.WriteLine("usage: WpfRender <p3d> <paa> <out.png>  |  --window <out.png>"); return 2; }

        var mesh = OdolLodReader.ReadAnyVisualLod(File.ReadAllBytes(args[0]));
        if (mesh is null) { Console.WriteLine("mesh decode returned null"); return 1; }

        var paa = PaaImage.LoadFile(args[1]);
        var bmp = BitmapSource.Create(paa.Width, paa.Height, 96, 96, PixelFormats.Bgra32, null,
            paa.Bgra, paa.Width * 4);
        bmp.Freeze();

        var groups = OdolMeshPreview.BuildGroups(mesh, null, null);
        var model = ModelViewHelper.Build(mesh, groups, _ => bmp);
        if (model is null) { Console.WriteLine("ModelViewHelper.Build returned null"); return 1; }

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
        Console.WriteLine($"Rendered {mesh.Points.Length} pts / {mesh.Faces.Count} faces, tex {paa.Width}x{paa.Height} -> {args[2]}");
        return 0;
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
