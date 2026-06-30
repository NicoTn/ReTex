using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace ReTex.App.Converters;

/// <summary>Shows just the file name of a full path (used for the PBO list).</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? Path.GetFileName(s) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
