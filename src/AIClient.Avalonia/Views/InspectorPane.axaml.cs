using System;
using AIClient.Avalonia.ViewModels;
using AIClient.Avalonia.ViewModels.Canvas;
using AIClient.Domain.Graph;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;

namespace AIClient.Avalonia.Views;

/// <summary>
/// The inspector's view half. The buttons here know the shown node's id, which the view
/// model deliberately does not track as bindable state - so the ask/reveal actions are
/// wired in code rather than stretched through bindings.
/// </summary>
public partial class InspectorPane : UserControl
{
    public InspectorPane()
    {
        InitializeComponent();

        RevealButton.Click += (_, _) => ((InspectorViewModel)DataContext!).RaiseOpenSource();
        AskButton.Click += (_, _) => Ask(null);
        ExplainButton.Click += (_, _) => Ask("explain");
        ProblemsButton.Click += (_, _) => Ask("problems");
    }

    private InspectorViewModel Inspector => (InspectorViewModel)DataContext!;

    private CanvasViewModel? Canvas => App.Services.GetService<CanvasViewModel>();

    private void Ask(string? action)
    {
        var canvas = Canvas;

        if (canvas is null || !canvas.HasSelection)
        {
            return;
        }

        Inspector.AskAbout(
            GraphSelection.Nodes(canvas.SelectedIds, 2),
            action,
            canvas.SelectionStatus);
    }
}
