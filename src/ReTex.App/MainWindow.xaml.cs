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
        Loaded += (_, _) =>
        {
            var vm = (MainViewModel)DataContext;
            vm.CheckFirstRunSetup(this);
            // Re-fit the camera whenever a new 3D model is loaded.
            vm.PropertyChanged += OnViewModelPropertyChanged;
        };
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
