using System.Globalization;
using System.Windows.Data;
using AIClient.Application.Configuration;

namespace AIClient.App.Converters;

/// <summary>
/// Renders a <see cref="ThemeMode"/> as its localized name. The picker binds the enum itself,
/// so the choice round-trips cleanly; only the display goes through the string table.
/// </summary>
public sealed class ThemeModeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ThemeMode mode ? Services.Localization.T($"S.Theme.{mode}") : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
