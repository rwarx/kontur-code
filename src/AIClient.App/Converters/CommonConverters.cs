using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AIClient.App.Converters;

/// <summary>Bool to <see cref="Visibility"/>. <c>Invert</c> flips the mapping.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible != Invert;
}

/// <summary>Non-empty string to <see cref="Visibility"/>. Hides labels that have nothing to say.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);

        if (Invert)
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Non-null to <see cref="Visibility"/>.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null;

        if (Invert)
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a bool, for enabling a control when a flag is false.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Non-empty string to bool, for <c>IsOpen</c> bindings that take a flag, not a visibility.</summary>
public sealed class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Message role to horizontal alignment: user content sits on the right, assistant content
/// spans the pane. Done as a converter so the message template stays one tree for both roles.
/// </summary>
public sealed class UserAlignmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A timestamp as "just now" / "14:32" / "Yesterday" / "3 Sep", the way a chat list reads.
/// </summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset timestamp)
        {
            return string.Empty;
        }

        var local = timestamp.ToLocalTime();
        var now = DateTimeOffset.Now;
        var age = now - local;

        return age switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes}m ago",
            _ when local.Date == now.Date => local.ToString("HH:mm", culture),
            _ when local.Date == now.Date.AddDays(-1) => "Yesterday",
            { TotalDays: < 7 } => local.ToString("dddd", culture),
            _ when local.Year == now.Year => local.ToString("d MMM", culture),
            _ => local.ToString("d MMM yyyy", culture),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Token counts and timing as a single subdued line under an answer.</summary>
public sealed class UsageSummaryConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3)
        {
            return string.Empty;
        }

        var parts = new List<string>(3);

        if (values[0] is int input and > 0)
        {
            parts.Add($"{input:N0} in");
        }

        if (values[1] is int output and > 0)
        {
            parts.Add($"{output:N0} out");
        }

        if (values[2] is int elapsed and > 0)
        {
            parts.Add(elapsed >= 1000 ? $"{elapsed / 1000.0:0.0}s" : $"{elapsed}ms");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Context window as "128K" rather than "128000", for the model picker badge.</summary>
public sealed class ContextWindowConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            int tokens and >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
            int tokens and >= 1_000 => $"{tokens / 1000}K",
            int tokens and > 0 => tokens.ToString(culture),
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Per-million pricing as "$3.00/M", or "Free" when the provider publishes a zero price.</summary>
public sealed class PriceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            decimal price and 0 => "Free",
            decimal price and > 0 => $"${price:0.##}/M",
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// int to double? and back, for <c>ui:NumberBox.Value</c>.
/// </summary>
/// <remarks>
/// The control is nullable-double because an empty box has no value; the settings it edits
/// are counts and are int. Without this the binding fails silently and the box shows blank.
/// </remarks>
public sealed class IntToDoubleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int number ? (double)number : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            // A partially typed value can be out of int range; leaving the source alone is
            // better than throwing inside the binding engine.
            double number when number is >= int.MinValue and <= int.MaxValue => (int)Math.Round(number),
            _ => Binding.DoNothing,
        };
}

/// <summary>Maps an equality test to a bool, for radio-style bindings over an enum.</summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null
            ? parameter
            : Binding.DoNothing;
}
