using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIClient.App.ViewModels;

namespace AIClient.App.Behaviors;

/// <summary>
/// Connects a <see cref="PasswordBox"/> to a <see cref="ProviderSettingsViewModel"/> without
/// binding the password.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PasswordBox.Password"/> is deliberately not a dependency property, and that is
/// the whole reason this class exists. Section 11 asks that keys are never displayed and
/// never linger: a bound TextBox would put the plaintext in the visual tree, in the binding
/// engine's value cache, and within reach of any automation client.
/// </para>
/// <para>
/// So the value moves one way only - box to ViewModel, on each keystroke - and the box is
/// emptied the moment the ViewModel clears its own copy, which it does immediately after
/// Save and on Cancel. Both Settings and the first-run wizard use this, so the rule has one
/// implementation rather than two that can drift apart.
/// </para>
/// </remarks>
public static class ApiKeyBox
{
    public static readonly DependencyProperty AttachProperty =
        DependencyProperty.RegisterAttached(
            "Attach",
            typeof(bool),
            typeof(ApiKeyBox),
            new PropertyMetadata(false, OnAttachChanged));

    /// <summary>Per-box state, so nothing about one row is kept in a static collection.</summary>
    private static readonly DependencyProperty BindingStateProperty =
        DependencyProperty.RegisterAttached(
            "BindingState",
            typeof(BoxState),
            typeof(ApiKeyBox),
            new PropertyMetadata(null));

    public static void SetAttach(DependencyObject element, bool value) =>
        element.SetValue(AttachProperty, value);

    public static bool GetAttach(DependencyObject element) =>
        (bool)element.GetValue(AttachProperty);

    private static void OnAttachChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }

        if (e.NewValue is true)
        {
            box.Loaded += OnLoaded;
            box.Unloaded += OnUnloaded;
            box.PasswordChanged += OnPasswordChanged;
            box.KeyDown += OnKeyDown;
        }
        else
        {
            box.Loaded -= OnLoaded;
            box.Unloaded -= OnUnloaded;
            box.PasswordChanged -= OnPasswordChanged;
            box.KeyDown -= OnKeyDown;

            Detach(box);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box || box.DataContext is not ProviderSettingsViewModel row)
        {
            return;
        }

        // A recycled container can be loaded again against a different row.
        Detach(box);

        var state = new BoxState(box, row);
        box.SetValue(BindingStateProperty, state);
        row.PropertyChanged += state.OnRowChanged;

        box.Clear();
        box.Focus();
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            Detach(box);

            // Collapsing the entry area must not leave the key sitting in a control that
            // WPF may reuse for another row.
            box.Clear();
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && Row(box) is { } row)
        {
            row.ApiKeyInput = box.Password;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not PasswordBox box || Row(box) is not { } row)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when row.SaveApiKeyCommand.CanExecute(null):
                e.Handled = true;
                row.SaveApiKeyCommand.Execute(null);
                break;

            case Key.Escape:
                e.Handled = true;
                row.CancelEditApiKeyCommand.Execute(null);
                break;
        }
    }

    private static ProviderSettingsViewModel? Row(PasswordBox box) =>
        (box.GetValue(BindingStateProperty) as BoxState)?.Row;

    private static void Detach(PasswordBox box)
    {
        if (box.GetValue(BindingStateProperty) is BoxState state)
        {
            state.Row.PropertyChanged -= state.OnRowChanged;
            box.ClearValue(BindingStateProperty);
        }
    }

    /// <summary>
    /// Holds the box-to-row pairing and mirrors the ViewModel clearing its copy of the key
    /// back into the control, so the plaintext does not outlive the Save that consumed it.
    /// </summary>
    private sealed class BoxState(PasswordBox box, ProviderSettingsViewModel row)
    {
        public ProviderSettingsViewModel Row { get; } = row;

        public void OnRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProviderSettingsViewModel.ApiKeyInput) &&
                Row.ApiKeyInput.Length == 0 &&
                box.SecurePassword.Length > 0)
            {
                box.Clear();
            }
        }
    }
}
