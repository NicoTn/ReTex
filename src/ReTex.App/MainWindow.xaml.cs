using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Xml;
using HelixToolkit.Wpf;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ReTex.App.ViewModels;

namespace ReTex.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        LoadConfigHighlighting();
        RestoreWindowState();
        Loaded += (_, _) =>
        {
            var vm = (MainViewModel)DataContext;
            vm.CheckFirstRunSetup(this);
            // Re-fit the camera whenever a new 3D model is loaded.
            vm.PropertyChanged += OnViewModelPropertyChanged;
        };
        Closing += (_, _) =>
        {
            SaveWindowState();
            ((MainViewModel)DataContext).Dispose();
        };

        // Hover-to-identify: name the texture of the model part under the cursor.
        Viewport.MouseMove += Viewport_MouseMove;
        Viewport.MouseLeave += (_, _) => ((MainViewModel)DataContext).HoverTexture = "";
        // Ctrl+click a spot on the model to locate the corresponding point on the 2D texture.
        Viewport.PreviewMouseLeftButtonDown += Viewport_PickUv;
        // Keep the pick marker glued to its texel when the preview image resizes.
        TexturePreviewImage.SizeChanged += (_, _) => UpdatePickMarker();
    }

    // ---- 3D -> texture pick (Ctrl+click): find where the clicked surface samples the atlas ----
    private bool _hasPick;
    private double _pickU, _pickV;

    private void Viewport_PickUv(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return; // plain drag still orbits the camera
        var vm = (MainViewModel)DataContext;
        var mesh = vm.CachedMesh;
        if (mesh is null || mesh.Uv.Length == 0) return;
        try
        {
            var hits = Viewport3DHelper.FindHits(Viewport.Viewport, e.GetPosition(Viewport));
            var hit = hits.FirstOrDefault(h => h.RayHit is RayMeshGeometry3DHitTestResult);
            if (hit?.RayHit is not RayMeshGeometry3DHitTestResult rh) return;
            int i1 = rh.VertexIndex1, i2 = rh.VertexIndex2, i3 = rh.VertexIndex3;
            if (i1 >= mesh.Uv.Length || i2 >= mesh.Uv.Length || i3 >= mesh.Uv.Length) return;
            // Barycentric interpolation of the hit triangle's UVs → exact texture coordinate. Vertex
            // indices align with mesh.Uv (raw, transform-independent), so a UV-debug tweak doesn't skew it.
            double w1 = rh.VertexWeight1, w2 = rh.VertexWeight2, w3 = rh.VertexWeight3;
            double u = w1 * mesh.Uv[i1][0] + w2 * mesh.Uv[i2][0] + w3 * mesh.Uv[i3][0];
            double v = w1 * mesh.Uv[i1][1] + w2 * mesh.Uv[i2][1] + w3 * mesh.Uv[i3][1];
            _pickU = u - Math.Floor(u); _pickV = v - Math.Floor(v); // wrap tiled UVs into [0,1)
            _hasPick = true;

            int pw = vm.PreviewImage?.PixelWidth ?? 0, ph = vm.PreviewImage?.PixelHeight ?? 0;
            vm.PickInfo = pw > 0
                ? $"Picked spot → U {_pickU:0.000}, V {_pickV:0.000}  →  pixel ({(int)(_pickU * pw)}, {(int)(_pickV * ph)}) of {pw}×{ph}"
                : $"Picked spot → U {_pickU:0.000}, V {_pickV:0.000}";
            PreviewTabs.SelectedIndex = 0; // show the Texture tab so the marker is visible
            // Defer until the (previously-collapsed) Texture tab has laid out, else ActualWidth is stale
            // and the crosshair lands in the wrong place.
            Dispatcher.BeginInvoke(new Action(UpdatePickMarker), System.Windows.Threading.DispatcherPriority.Loaded);
            e.Handled = true;
        }
        catch { /* ignore stray hit-test failures */ }
    }

    /// <summary>Positions the crosshair over the picked texel, accounting for the Uniform-stretch
    /// letterboxing of the texture image. The shared render transform handles zoom/pan.</summary>
    private void UpdatePickMarker()
    {
        var vm = DataContext as MainViewModel;
        var src = vm?.PreviewImage;
        double cw = TexturePreviewImage.ActualWidth, ch = TexturePreviewImage.ActualHeight;
        if (!_hasPick || src is null || cw <= 0 || ch <= 0 || src.PixelWidth <= 0 || src.PixelHeight <= 0)
        {
            PickMarker.Visibility = Visibility.Collapsed;
            return;
        }
        double scale = Math.Min(cw / src.PixelWidth, ch / src.PixelHeight);
        double dispW = src.PixelWidth * scale, dispH = src.PixelHeight * scale;
        double ox = (cw - dispW) / 2, oy = (ch - dispH) / 2;
        Canvas.SetLeft(PickMarker, ox + _pickU * dispW - PickMarker.Width / 2);
        Canvas.SetTop(PickMarker, oy + _pickV * dispH - PickMarker.Height / 2);
        PickMarker.Visibility = Visibility.Visible;
    }

    /// <summary>Hands the current (retextured) model off to an external viewer such as P3D Analyzer:
    /// exports a virtual-path mirror of the model + textures, then launches the configured viewer exe
    /// on the .p3d. Prompts for the viewer location the first time and remembers it.</summary>
    private void OpenInViewer_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        var settings = vm.Settings;
        var exe = settings.P3dViewerPath;
        if (string.IsNullOrWhiteSpace(exe) || !System.IO.File.Exists(exe))
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Locate your model viewer (e.g. P3D Analyzer .exe)",
                Filter = "Executable (*.exe)|*.exe"
            };
            if (dlg.ShowDialog(this) != true) return;
            exe = dlg.FileName;
            settings.P3dViewerPath = exe;
            settings.Save();
        }
        var modelPath = vm.ExportModelForViewer();
        if (modelPath is null) return; // export already reported the reason
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ReTex", "viewer");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"\"{modelPath}\"") { UseShellExecute = true });
            vm.SetStatus($"Opened in {System.IO.Path.GetFileNameWithoutExtension(exe)}. If textures look wrong, point that viewer's texture/P: root at: {root}", StatusSeverity.Info);
        }
        catch (Exception ex)
        {
            // Launch failed (bad exe / it doesn't take a file arg) — reveal the export so they can open it manually.
            vm.SetStatus($"Couldn't launch the viewer ({ex.Message}). Model exported to: {modelPath}", StatusSeverity.Warn);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{modelPath}\"")); } catch { }
        }
    }

    /// <summary>Reverse pick: the user Ctrl+clicked a spot on the 2D texture. Convert the click to a
    /// texture (u,v), drop the crosshair there, glow the geometry that samples it on the model, and
    /// switch to the 3D tab so they can see which body part that texture region maps to.</summary>
    private void ReversePickAt(Point pImg)
    {
        var vm = (MainViewModel)DataContext;
        var src = vm.PreviewImage;
        double cw = TexturePreviewImage.ActualWidth, ch = TexturePreviewImage.ActualHeight;
        if (src is null || cw <= 0 || ch <= 0 || src.PixelWidth <= 0 || src.PixelHeight <= 0) return;
        double scale = Math.Min(cw / src.PixelWidth, ch / src.PixelHeight);
        double dispW = src.PixelWidth * scale, dispH = src.PixelHeight * scale;
        double ox = (cw - dispW) / 2, oy = (ch - dispH) / 2;
        double u = (pImg.X - ox) / dispW, v = (pImg.Y - oy) / dispH;
        if (u < 0 || u > 1 || v < 0 || v > 1) return; // clicked in the letterbox, not the texture

        _pickU = u; _pickV = v; _hasPick = true;
        UpdatePickMarker(); // crosshair where they clicked (stays for when they return to this tab)
        bool found = vm.HighlightUv(u, v);
        vm.PickInfo = $"Texture U {u:0.000}, V {v:0.000} → pixel ({(int)(u * src.PixelWidth)}, {(int)(v * src.PixelHeight)})"
            + (found ? " — the glowing part on the 3D model uses this spot" : " — no geometry samples this spot");
        PreviewTabs.SelectedIndex = 1; // show the 3D tab so the highlight is visible
    }

    // ---- 2D texture preview zoom/pan (wheel = zoom about cursor, left-drag = pan, right-click = reset) ----
    private bool _texPanning;
    private Point _texPanStart;
    private double _texPanStartX, _texPanStartY;
    private const double TexMinScale = 1.0, TexMaxScale = 16.0;

    private void TexturePreview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double s = TextureScale.ScaleX <= 0 ? 1.0 : TextureScale.ScaleX;
        double factor = e.Delta > 0 ? 1.2 : 1.0 / 1.2;
        double s2 = Math.Clamp(s * factor, TexMinScale, TexMaxScale);
        if (Math.Abs(s2 - s) < 1e-6) return;
        var c = e.GetPosition(TexturePreviewViewport);
        // Keep the point under the cursor fixed: T' = c - (s'/s)(c - T).
        TextureTranslate.X = c.X - (s2 / s) * (c.X - TextureTranslate.X);
        TextureTranslate.Y = c.Y - (s2 / s) * (c.Y - TextureTranslate.Y);
        TextureScale.ScaleX = TextureScale.ScaleY = s2;
        if (s2 <= TexMinScale + 1e-6) ResetTextureView(); // snap back to a centred fit
        e.Handled = true;
    }

    private void TexturePreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ctrl+click = reverse pick: mark this texel and glow the geometry that samples it on the model.
        if (Keyboard.Modifiers == ModifierKeys.Control) { ReversePickAt(e.GetPosition(TexturePreviewImage)); e.Handled = true; return; }
        if (TextureScale.ScaleX <= TexMinScale + 1e-6) return; // nothing to pan at fit scale
        _texPanning = true;
        _texPanStart = e.GetPosition(TexturePreviewViewport);
        _texPanStartX = TextureTranslate.X;
        _texPanStartY = TextureTranslate.Y;
        TexturePreviewViewport.CaptureMouse();
    }

    private void TexturePreview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_texPanning) return;
        var p = e.GetPosition(TexturePreviewViewport);
        TextureTranslate.X = _texPanStartX + (p.X - _texPanStart.X);
        TextureTranslate.Y = _texPanStartY + (p.Y - _texPanStart.Y);
    }

    private void TexturePreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _texPanning = false;
        TexturePreviewViewport.ReleaseMouseCapture();
    }

    private void TexturePreview_Reset(object sender, MouseButtonEventArgs e) => ResetTextureView();

    private void ResetTextureView()
    {
        TextureScale.ScaleX = TextureScale.ScaleY = 1.0;
        TextureTranslate.X = TextureTranslate.Y = 0;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        var labels = vm.PreviewPartLabels;
        if (labels is null) { vm.HoverTexture = ""; return; }
        try
        {
            var hits = Viewport3DHelper.FindHits(Viewport.Viewport, e.GetPosition(Viewport));
            var hit = hits.FirstOrDefault(h => h.Model is GeometryModel3D gm && labels.ContainsKey(gm));
            vm.HoverTexture = hit?.Model is GeometryModel3D g && labels.TryGetValue(g, out var label) ? label : "";
        }
        catch { vm.HoverTexture = ""; }
    }

    /// <summary>Restores the persisted window geometry + the browse/config row split, guarding
    /// against off-screen positions (a monitor that's since been removed).</summary>
    private void RestoreWindowState()
    {
        var s = ((MainViewModel)DataContext).Settings;
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) && IsOnScreen(s.WindowLeft, s.WindowTop, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }
        if (s.BrowseRowHeight > 0)
            BrowseRow.Height = new GridLength(s.BrowseRowHeight, GridUnitType.Star);
        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowState()
    {
        var s = ((MainViewModel)DataContext).Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        // RestoreBounds holds the pre-maximize geometry; use it so we never persist a maximized frame.
        var b = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        s.WindowWidth = b.Width;
        s.WindowHeight = b.Height;
        s.WindowLeft = b.Left;
        s.WindowTop = b.Top;
        s.BrowseRowHeight = BrowseRow.Height.IsStar ? BrowseRow.Height.Value : 0;
        s.Save();
    }

    /// <summary>True when the given frame overlaps the combined virtual desktop (guards against a
    /// saved position on a monitor that's since been disconnected). Pure WPF — no WinForms dependency.</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var virtualDesktop = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return virtualDesktop.IntersectsWith(new Rect(left, top, width, height));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewModel3D))
        {
            var vm = (MainViewModel)DataContext;
            bool preserve = vm.PreserveCameraOnNextPreview;
            vm.PreserveCameraOnNextPreview = false;   // one-shot
            // Re-fit only on a genuine model change; a live UV-debug tweak keeps the current camera.
            if (vm.PreviewModel3D != null && !preserve)
                Dispatcher.BeginInvoke(new Action(() => Viewport.ZoomExtents()), System.Windows.Threading.DispatcherPriority.Background);
            // A genuine model change invalidates the pick crosshair.
            if (!preserve) { _hasPick = false; UpdatePickMarker(); }
        }
        // Reset the 2D zoom/pan whenever a different texture is shown so the new one starts fit + centred.
        if (e.PropertyName == nameof(MainViewModel.PreviewImage))
        {
            ResetTextureView();
            UpdatePickMarker(); // texture size may differ → reposition the pick crosshair
        }
    }

    // Load the embedded Arma config.cpp grammar into the config editor.
    private void LoadConfigHighlighting()
    {
        var asm = typeof(MainWindow).Assembly;
        using var stream = asm.GetManifestResourceStream("ReTex.App.ArmaConfig.xshd");
        if (stream is null) return;
        using var reader = new XmlTextReader(stream);
        ConfigEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
