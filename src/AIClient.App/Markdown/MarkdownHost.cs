using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using AIClient.Application.Markdown;

namespace AIClient.App.Markdown;

/// <summary>
/// Attached properties that turn the parsed block model into WPF inline content.
/// </summary>
/// <remarks>
/// Three parts of Markdown cannot be expressed as a fixed XAML tree, because their shape
/// depends on the data: a paragraph's styled runs, a code block's coloured tokens, and a
/// table's column count. Those three are built here; everything else stays a DataTemplate
/// in <c>MarkdownTemplates.xaml</c>.
///
/// The alternative - an <c>ItemsControl</c> per line of code, or per inline run - produces
/// hundreds of containers for a single answer and is what makes a streaming transcript
/// crawl. A <see cref="TextBlock"/> filled with <see cref="Run"/>s is one element.
/// </remarks>
public static class MarkdownHost
{
    /// <summary>
    /// Whether code blocks are coloured. Inherited, so <c>ChatView</c> sets it once at its
    /// root and every code block below picks it up, including blocks nested in list items.
    /// </summary>
    public static readonly DependencyProperty HighlightCodeProperty =
        DependencyProperty.RegisterAttached(
            "HighlightCode",
            typeof(bool),
            typeof(MarkdownHost),
            new FrameworkPropertyMetadata(
                true,
                FrameworkPropertyMetadataOptions.Inherits,
                OnHighlightCodeChanged));

    /// <summary>Inline spans of a paragraph, heading, list item or table cell.</summary>
    public static readonly DependencyProperty SpansProperty =
        DependencyProperty.RegisterAttached(
            "Spans",
            typeof(IReadOnlyList<InlineSpan>),
            typeof(MarkdownHost),
            new PropertyMetadata(null, OnSpansChanged));

    /// <summary>Tokenized source lines of a code block.</summary>
    public static readonly DependencyProperty CodeLinesProperty =
        DependencyProperty.RegisterAttached(
            "CodeLines",
            typeof(IReadOnlyList<IReadOnlyList<CodeToken>>),
            typeof(MarkdownHost),
            new PropertyMetadata(null, OnCodeLinesChanged));

    /// <summary>A pipe table, projected onto the <see cref="Grid"/> this is set on.</summary>
    public static readonly DependencyProperty TableProperty =
        DependencyProperty.RegisterAttached(
            "Table",
            typeof(TableBlock),
            typeof(MarkdownHost),
            new PropertyMetadata(null, OnTableChanged));

    public static bool GetHighlightCode(DependencyObject element) =>
        (bool)element.GetValue(HighlightCodeProperty);

    public static void SetHighlightCode(DependencyObject element, bool value) =>
        element.SetValue(HighlightCodeProperty, value);

    public static IReadOnlyList<InlineSpan>? GetSpans(DependencyObject element) =>
        (IReadOnlyList<InlineSpan>?)element.GetValue(SpansProperty);

    public static void SetSpans(DependencyObject element, IReadOnlyList<InlineSpan>? value) =>
        element.SetValue(SpansProperty, value);

    public static IReadOnlyList<IReadOnlyList<CodeToken>>? GetCodeLines(DependencyObject element) =>
        (IReadOnlyList<IReadOnlyList<CodeToken>>?)element.GetValue(CodeLinesProperty);

    public static void SetCodeLines(DependencyObject element, IReadOnlyList<IReadOnlyList<CodeToken>>? value) =>
        element.SetValue(CodeLinesProperty, value);

    public static TableBlock? GetTable(DependencyObject element) =>
        (TableBlock?)element.GetValue(TableProperty);

    public static void SetTable(DependencyObject element, TableBlock? value) =>
        element.SetValue(TableProperty, value);

    private static void OnSpansChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock target)
        {
            return;
        }

        target.Inlines.Clear();

        if (e.NewValue is not IReadOnlyList<InlineSpan> spans)
        {
            return;
        }

        foreach (var span in spans)
        {
            target.Inlines.Add(BuildInline(span, target));
        }
    }

    private static Inline BuildInline(InlineSpan span, TextBlock host)
    {
        Inline inline = new Run(span.Text);

        if (span.Style.HasFlag(InlineStyle.Code))
        {
            var run = (Run)inline;
            run.FontFamily = ResolveFont(host, "CodeFont");
            run.Background = ResolveBrush(host, "ControlFillColorSecondaryBrush");

            // Slightly smaller: a monospace face at the surrounding size reads as oversized
            // next to proportional text.
            run.FontSize = host.FontSize * 0.92;
        }

        if (span.Style.HasFlag(InlineStyle.Bold))
        {
            inline = new Bold(inline);
        }

        if (span.Style.HasFlag(InlineStyle.Italic))
        {
            inline = new Italic(inline);
        }

        if (span.Style.HasFlag(InlineStyle.Strikethrough))
        {
            inline.TextDecorations = TextDecorations.Strikethrough;
        }

        if (span.Style.HasFlag(InlineStyle.Link) && span.Url is { Length: > 0 })
        {
            var link = new Hyperlink(inline) { ToolTip = span.Url };

            // Only well-formed absolute http(s) URLs become clickable. A relative or
            // exotic-scheme target from model output has no business being handed to
            // ShellExecute.
            if (Uri.TryCreate(span.Url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                link.NavigateUri = uri;
                link.RequestNavigate += OnRequestNavigate;
            }

            inline = link;
        }

        return inline;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // No browser, or the shell refused. Nothing actionable, and a dialog here would
            // be a worse outcome than a link that does nothing.
        }
    }

    private static void OnCodeLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock target)
        {
            RenderCode(target, e.NewValue as IReadOnlyList<IReadOnlyList<CodeToken>>);
        }
    }

    private static void OnHighlightCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Inherited, so this fires on every descendant. Only the code blocks care.
        if (d is TextBlock target && GetCodeLines(target) is { } lines)
        {
            RenderCode(target, lines);
        }
    }

    private static void RenderCode(TextBlock target, IReadOnlyList<IReadOnlyList<CodeToken>>? lines)
    {
        target.Inlines.Clear();

        if (lines is null || lines.Count == 0)
        {
            return;
        }

        var highlight = GetHighlightCode(target);

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                target.Inlines.Add(new LineBreak());
            }

            foreach (var token in lines[i])
            {
                var run = new Run(token.Text);

                if (highlight && ResolveTokenBrush(target, token.Kind) is { } brush)
                {
                    run.Foreground = brush;
                }

                if (token.Kind == CodeTokenKind.Comment)
                {
                    run.FontStyle = FontStyles.Italic;
                }

                target.Inlines.Add(run);
            }
        }
    }

    private static Brush? ResolveTokenBrush(FrameworkElement host, CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => ResolveBrush(host, "SyntaxKeywordBrush"),
        CodeTokenKind.Type => ResolveBrush(host, "SyntaxTypeBrush"),
        CodeTokenKind.String => ResolveBrush(host, "SyntaxStringBrush"),
        CodeTokenKind.Number => ResolveBrush(host, "SyntaxNumberBrush"),
        CodeTokenKind.Comment => ResolveBrush(host, "SyntaxCommentBrush"),
        CodeTokenKind.Directive => ResolveBrush(host, "SyntaxDirectiveBrush"),
        CodeTokenKind.Function => ResolveBrush(host, "SyntaxFunctionBrush"),
        _ => null,
    };

    private static void OnTableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid)
        {
            return;
        }

        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();

        if (e.NewValue is not TableBlock table)
        {
            return;
        }

        var columns = table.Headers.Count;

        foreach (var row in table.Rows)
        {
            columns = Math.Max(columns, row.Count);
        }

        if (columns == 0)
        {
            return;
        }

        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var separator = ResolveBrush(grid, "ControlStrokeColorDefaultBrush");

        AddRow(grid, table.Headers, columns, rowIndex: 0, isHeader: true, separator);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            AddRow(grid, table.Rows[r], columns, r + 1, isHeader: false, separator);
        }
    }

    private static void AddRow(
        Grid grid,
        IReadOnlyList<IReadOnlyList<InlineSpan>> cells,
        int columns,
        int rowIndex,
        bool isHeader,
        Brush? separator)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var c = 0; c < columns; c++)
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 6, 10, 6),
                FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
            };

            if (c < cells.Count)
            {
                SetSpans(text, cells[c]);
            }

            var border = new Border
            {
                Child = text,
                BorderBrush = separator,
                BorderThickness = new Thickness(0, 0, 0, isHeader ? 1 : 0),
            };

            Grid.SetRow(border, rowIndex);
            Grid.SetColumn(border, c);
            grid.Children.Add(border);
        }
    }

    private static Brush? ResolveBrush(FrameworkElement host, string key) =>
        host.TryFindResource(key) as Brush;

    private static FontFamily ResolveFont(FrameworkElement host, string key) =>
        host.TryFindResource(key) as FontFamily ?? new FontFamily("Consolas");
}
