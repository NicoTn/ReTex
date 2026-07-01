using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ReTex.App.Converters;

/// <summary>Green for a detected/valid path, amber for a problem (used in the Settings window).</summary>
public sealed class BoolToStatusBrushConverter : IValueConverter
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly Brush Problem = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Ok : Problem;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
