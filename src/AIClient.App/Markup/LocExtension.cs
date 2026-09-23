using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace AIClient.App.Markup;

/// <summary>
/// Localized text for a Binding's <c>FallbackValue</c>/<c>TargetNullValue</c> slot.
/// <c>DynamicResource</c> cannot live in those slots: they belong to the Binding, which
/// is not a DependencyObject, so the parser throws the moment the view loads. This hands
/// back a Binding that reads the live string table when the fallback actually fires -
/// which is also after the table is merged, unlike parse time.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return key;
        }

        return new Binding
        {
            Path = new PropertyPath("Resources[" + key + "]"),
            Source = app,
        };
    }
}
