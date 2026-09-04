using Microsoft.Extensions.Logging;

namespace AIClient.Tests.Support;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that keeps every line it was asked to write, so a test
/// can assert on what a component logged.
/// </summary>
/// <remarks>
/// Section 26 forbids an API key, an Authorization header or any other secret from ever reaching
/// the log. That is a claim about output, and the only honest way to check it is to capture the
/// output and look at it: <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/>
/// would let such a regression through in silence.
/// </remarks>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    /// <summary>Every line written so far, most recent last.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>Everything written, joined - for a single "does this ever appear" assertion.</summary>
    public string Text => string.Join(Environment.NewLine, Lines);

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    /// <summary>
    /// Nothing is filtered out. A test asserting that a secret never appears has to see the
    /// Trace and Debug calls too, since careless logging is likeliest to hide there.
    /// </summary>
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        // The exception is appended because a stack trace or an inner message is part of the
        // output a secret could leak through.
        var line = exception is null
            ? $"[{logLevel}] {formatter(state, exception)}"
            : $"[{logLevel}] {formatter(state, exception)} {exception}";

        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
