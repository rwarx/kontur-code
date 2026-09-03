using System.Globalization;
using System.Windows.Data;

namespace AIClient.App.Markdown;

/// <summary>
/// Heading level to font size, relative to the chat font size passed as the second value.
/// </summary>
/// <remarks>
/// Multiplicative rather than a fixed table so headings scale with the user's chosen chat
/// font size instead of staying at whatever looked right at 14 pixels.
/// </remarks>
public sealed class HeadingFontSizeConverter : IMultiValueConverter
{
    private static readonly double[] Scale = [1.55, 1.35, 1.2, 1.1, 1.0, 0.95];

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = values.Length > 0 && values[0] is int l ? Math.Clamp(l, 1, 6) : 1;
        var baseSize = values.Length > 1 && values[1] is double size and > 0 ? size : 14.0;

        return Math.Round(baseSize * Scale[level - 1], 1);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
