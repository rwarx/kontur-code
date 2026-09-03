namespace AIClient.App.Services;

/// <summary>
/// Marshals work onto the UI thread for callbacks that arrive from somewhere else.
/// </summary>
/// <remarks>
/// Application and Infrastructure services raise their events on whatever thread happened to
/// finish the work - a thread-pool thread, in practice. That is the right design: those layers
/// know nothing about WPF and should not. It does mean every subscriber in this layer owns the
/// hop back, and getting it wrong fails in two different ways depending on what is touched.
/// A bound scalar property survives, because WPF quietly marshals a single property change.
/// An <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> behind a
/// <see cref="System.Windows.Data.CollectionView"/> throws outright. The difference is not
/// something to rediscover per call site, hence this.
///
/// <c>ConfigureAwait(true)</c> is not a substitute. It resumes on the context the method is
/// already running on, and a thread-pool thread has none - so an await inside a handler that
/// was invoked off-thread stays off-thread the whole way down.
///
/// No state of its own: the dispatcher is read from the running application each time, and is
/// null under unit test, where running inline is the correct behaviour.
/// </remarks>
internal static class UiThread
{
    /// <summary>
    /// Runs an asynchronous operation on the UI thread and completes when it does.
    /// </summary>
    /// <remarks>
    /// The returned task is the operation's own, unwrapped from the dispatcher operation that
    /// scheduled it, so a failure inside <paramref name="operation"/> reaches the caller's
    /// try/catch instead of resurfacing later as an unobserved task exception.
    /// </remarks>
    public static Task RunAsync(Func<Task> operation)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        return dispatcher is null || dispatcher.CheckAccess()
            ? operation()
            : dispatcher.InvokeAsync(operation).Task.Unwrap();
    }

    /// <summary>Applies a state change on the UI thread, without waiting for it.</summary>
    public static void Post(Action update)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            update();
            return;
        }

        dispatcher.BeginInvoke(update);
    }
}
