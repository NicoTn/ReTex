using System.ComponentModel;
using System.Linq;
using System.Windows;
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
        Closing += (_, _) => SaveWindowState();

        // Hover-to-identify: name the texture of the model part under the cursor.
        Viewport.MouseMove += Viewport_MouseMove;
        Viewport.MouseLeave += (_, _) => ((MainViewModel)DataContext).HoverTexture = "";
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
        if (e.PropertyName == nameof(MainViewModel.PreviewModel3D) &&
            ((MainViewModel)DataContext).PreviewModel3D != null)
        {
            // defer so the model is attached to the visual tree before fitting
            Dispatcher.BeginInvoke(new Action(() => Viewport.ZoomExtents()), System.Windows.Threading.DispatcherPriority.Background);
        }
        // Reset the 2D zoom/pan whenever a different texture is shown so the new one starts fit + centred.
        if (e.PropertyName == nameof(MainViewModel.PreviewImage))
            ResetTextureView();
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
