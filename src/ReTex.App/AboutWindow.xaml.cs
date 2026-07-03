using System.Reflection;
using System.Windows;

namespace ReTex.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
