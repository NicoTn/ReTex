using System.Windows;
using ReTex.App.ViewModels;

namespace ReTex.App;

public partial class SettingsWindow : Window
{
    public Visibility IntroVisible { get; }
    public SettingsViewModel ViewModel { get; }

    /// <param name="settings">Settings to edit in place.</param>
    /// <param name="isFirstRun">Shows an explanatory banner when opened automatically because something wasn't detected.</param>
    public SettingsWindow(AppSettings settings, bool isFirstRun = false)
    {
        IntroVisible = isFirstRun ? Visibility.Visible : Visibility.Collapsed;
        ViewModel = new SettingsViewModel(settings);
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveCommand.Execute(null);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
