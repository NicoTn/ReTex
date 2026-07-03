using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ReTex.App.ViewModels;

namespace ReTex.App.Converters;

/// <summary>Colours the status line by severity: muted grey for info, amber for warnings, red for errors.</summary>
public sealed class StatusSeverityToBrushConverter : IValueConverter
{
    private static readonly Brush Info = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            StatusSeverity.Warn => Warn,
            StatusSeverity.Error => Error,
            _ => Info,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
