using System;
using System.Threading.Tasks;

namespace AIClient.Avalonia.Services;

/// <summary>
/// The one place this UI layer touches a dispatcher.
/// </summary>
/// <remarks>
/// The graph raises <c>Changed</c> off the UI thread by contract; every subscriber that
/// touches observable state hops through here. <see cref="Post"/> runs inline when already
/// on the UI thread, which is why a handler is safe to trigger from either world.
/// The <c>global::</c> prefixes matter: this namespace is itself called
/// <c>AIClient.Avalonia</c>, so an unqualified <c>Avalonia.Threading</c> would resolve
/// against it and fail.
/// </remarks>
public static class UiThread
{
    private static readonly global::Avalonia.Threading.Dispatcher Dispatcher =
        global::Avalonia.Threading.Dispatcher.UIThread;

    public static bool IsOnUiThread => Dispatcher.CheckAccess();

    /// <summary>Runs an action on the UI thread, inline when already there.</summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.Post(action);
        }
    }

    /// <summary>Awaits an action on the UI thread from anywhere.</summary>
    public static Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).GetTask();
}
