using Avalonia.Data.Converters;
using Avalonia.Data;
using System.Globalization;

namespace Aemeath.Desktop.Converters;

/// <summary>
/// Bool 到 Visibility 的转换器
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool boolValue && boolValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
