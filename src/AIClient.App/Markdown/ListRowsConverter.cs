using System.Globalization;
using System.Windows.Data;
using AIClient.Application.Markdown;

namespace AIClient.App.Markdown;

/// <summary>
/// Flattens a <see cref="ListBlock"/> into rows carrying their own marker text.
/// </summary>
/// <remarks>
/// XAML can bind a list, but it cannot count. Numbering an ordered list, restarting at the
/// declared start value and indenting by nesting level are all trivial in C# and clumsy in
/// markup, so the projection happens once here instead of in a pile of converters.
/// </remarks>
public sealed class ListRowsConverter : IValueConverter
{
    /// <summary>Indent per nesting level, in device-independent pixels.</summary>
    private const double IndentPerLevel = 18;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ListBlock list)
        {
            return Array.Empty<ListRow>();
        }

        var rows = new List<ListRow>(list.Items.Count);
        var number = list.StartNumber;

        foreach (var item in list.Items)
        {
            var marker = list.IsOrdered
                ? $"{number++}."
                : BulletFor(item.Level);

            rows.Add(new ListRow(marker, new System.Windows.Thickness(item.Level * IndentPerLevel, 0, 0, 0), item.Blocks));
        }

        return rows;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Alternating glyphs, the way a typeset document distinguishes nesting depth.</summary>
    private static string BulletFor(int level) => (level % 3) switch
    {
        0 => "•",
        1 => "◦",
        _ => "▪",
    };
}

/// <summary>One rendered list entry: its marker, its indent and its content blocks.</summary>
public sealed record ListRow(
    string Marker,
    System.Windows.Thickness Indent,
    IReadOnlyList<MarkdownBlock> Blocks);
