using System.ComponentModel;
using System.Windows;
using System.Xml;
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
