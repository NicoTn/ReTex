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
        Loaded += (_, _) => ((MainViewModel)DataContext).CheckFirstRunSetup(this);
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
