using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace ReTex.App.Converters;

/// <summary>Turns a project file path (…\MyProject\retex.json) into the project folder name ("MyProject")
/// for the recents menu.</summary>
public sealed class ProjectPathToNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0) return value;
        var dir = Path.GetDirectoryName(s);
        return string.IsNullOrEmpty(dir) ? s : Path.GetFileName(dir);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
