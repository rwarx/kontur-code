using System.Windows;

namespace AIClient.App.Converters;

/// <summary>
/// Carries a DataContext into places the visual tree does not reach.
/// </summary>
/// <remarks>
/// A <c>ContextMenu</c> lives in its own tree, so <c>RelativeSource AncestorType</c> finds
/// nothing from inside one and its items cannot see the ViewModel that owns the list. The
/// standard remedy is a <see cref="Freezable"/> placed in resources: resources inherit the
/// DataContext of their host, and the menu can reach them by <c>StaticResource</c>.
/// </remarks>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(
            nameof(Data),
            typeof(object),
            typeof(BindingProxy),
            new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
