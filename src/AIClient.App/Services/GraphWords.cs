using System.Globalization;
using AIClient.Domain.Graph;

namespace AIClient.App.Services;

/// <summary>Graph vocabulary in the active language, with count-aware forms where the culture has them.</summary>
public static class GraphWords
{
    /// <summary>A node kind noun, shaped for the count: "1 file|2 files", "1 файл|2 файла|5 файлов".</summary>
    public static string Kind(GraphNodeKind kind, int count) => Form("S.Kind." + kind, count);

    /// <summary>A node kind noun in the singular, for one inspected node or one list row.</summary>
    public static string Kind(GraphNodeKind kind) => Kind(kind, 1);

    /// <summary>An edge kind verb phrase as it reads between two nodes.</summary>
    public static string Edge(GraphEdgeKind kind) => Localization.T("S.Edge." + kind);

    /// <summary>
    /// Picks a pipe-separated form: two forms everywhere, three for Russian
    /// (singular | few | many), chosen by the usual Slavic count rules.
    /// </summary>
    private static string Form(string key, int count)
    {
        var forms = Localization.T(key).Split('|');

        if (forms.Length == 1)
        {
            return forms[0];
        }

        int index;

        if (forms.Length >= 3 && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru")
        {
            var n = Math.Abs(count) % 100;
            var d = n % 10;

            index = n is >= 11 and <= 14 ? 2
                : d == 1 ? 0
                : d is >= 2 and <= 4 ? 1
                : 2;
        }
        else
        {
            index = Math.Abs(count) == 1 ? 0 : 1;
        }

        return forms[Math.Min(index, forms.Length - 1)];
    }
}
