using System.Globalization;
using System.Windows.Data;

namespace DdoGearScanner;

/// <summary>Formats a UTC <see cref="DateTime"/> as local wall-clock time for the list.</summary>
public sealed class UtcToLocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime dt ? dt.ToLocalTime().ToString("HH:mm:ss") : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
