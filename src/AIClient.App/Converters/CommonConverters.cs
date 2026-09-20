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

/// <summary>
/// A token count as a star weight, for the context panel's stacked bar.
/// </summary>
/// <remarks>
/// The bar is a four-column <c>Grid</c> whose columns are weighted by what each band costs, so
/// the proportions come out of the layout pass instead of out of arithmetic over
/// <c>ActualWidth</c>. A band with no tokens weighs nothing and collapses on its own, which is
/// what a chat that has not run a tool yet should look like.
/// </remarks>
public sealed class TokenShareConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new GridLength(value is int tokens and > 0 ? tokens : 0, GridUnitType.Star);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A token count as "73 909", or an em dash when the provider never reported one.
/// </summary>
/// <remarks>
/// Grouped by the current culture on purpose: the panel is a wall of six-figure numbers and
/// "73909" is unreadable next to "73 909". Zero is printed as zero rather than as a dash - a
/// model that used no cache reported that, it did not stay silent.
/// </remarks>
public sealed class TokenCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            int tokens => tokens.ToString("N0", culture),
            long tokens => tokens.ToString("N0", culture),
            _ => "—",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A share of the window as "37%", or an em dash when the window is unknown.
/// </summary>
/// <remarks>
/// Rounded to whole percent because the panel is a glance, not a meter, and a figure that
/// changes in its second decimal while a stream runs reads as noise.
/// </remarks>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double share ? share.ToString("0'%'", culture) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A band's share of the estimated prompt as "59,4%", for the stacked bar's legend.
/// </summary>
/// <remarks>
/// One decimal rather than none, because the small bands are the interesting ones: "0,5%" and
/// "0,2%" both round to "0%" and the legend would then claim two bands are empty when they are
/// the reason the numbers do not add up.
/// </remarks>
public sealed class BandShareConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double share ? share.ToString("0.0'%'", culture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Money as "0,00 $", or an em dash when the catalogue publishes no prices.
/// </summary>
/// <remarks>
/// The dash matters more here than anywhere else in the panel: a model whose price nobody told
/// us about is not a free model, and "0,00 $" for the second one invents a guarantee.
/// </remarks>
public sealed class CostConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal cost ? cost.ToString("C2", culture) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A timestamp as "5 Sep 2026, 01:23" - the panel's two dates, in full.
/// </summary>
/// <remarks>
/// Not <see cref="RelativeTimeConverter"/>: "2d" is right for a sidebar row a user is scanning,
/// and wrong for a field labelled "created" that they opened the panel to read.
/// </remarks>
public sealed class AbsoluteTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset timestamp ? timestamp.ToLocalTime().ToString("g", culture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
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
