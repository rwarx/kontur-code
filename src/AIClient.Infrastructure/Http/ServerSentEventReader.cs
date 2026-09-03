using System.Runtime.CompilerServices;
using System.Text;

namespace AIClient.Infrastructure.Http;

/// <summary>
/// Reads a <c>text/event-stream</c> body and yields one payload per SSE event.
/// </summary>
/// <remarks>
/// Written by hand rather than taken from a library because the failure modes matter here.
/// A naive <c>ReadLineAsync</c> loop that splits on <c>\n</c> breaks the moment a provider
/// flushes a chunk mid-line, which they do routinely - the result is dropped tokens under
/// load and a subtly truncated answer. This reader accumulates across chunk boundaries and
/// only emits an event once its terminating blank line has actually arrived.
///
/// It also honours the token: the read is cancellable, so pressing Stop aborts the HTTP
/// response rather than waiting for the provider to finish sending.
/// </remarks>
public static class ServerSentEventReader
{
    /// <summary>
    /// Guards against a provider that never sends a blank line. Well past any real
    /// SSE payload, and small enough that a runaway response cannot exhaust memory.
    /// </summary>
    private const int MaxEventBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Yields the <c>data:</c> payload of each event, in order.
    /// Comment lines, retry hints and other fields are skipped; the terminal
    /// <c>[DONE]</c> sentinel is yielded so the caller can act on it.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        var data = new StringBuilder();

        // Looping on ReadLineAsync returning null rather than on EndOfStream: the latter
        // performs a synchronous read to find out whether more data is coming, which blocks
        // a thread for as long as the model takes to emit its next token.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                break;
            }

            // A blank line terminates the current event.
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString();
                    data.Clear();
                }

                continue;
            }

            // Comments keep the connection alive and carry no payload.
            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];

            if (!field.Equals("data", StringComparison.Ordinal))
            {
                // event:, id: and retry: are not used by any provider the app talks to.
                continue;
            }

            var value = colon < 0 ? string.Empty : line[(colon + 1)..];

            // A single leading space after the colon is part of the framing, not the data.
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            // Multiple data: lines in one event concatenate with a newline, per the spec.
            if (data.Length > 0)
            {
                data.Append('\n');
            }

            data.Append(value);

            if (data.Length > MaxEventBytes)
            {
                throw new InvalidDataException(
                    $"A server-sent event exceeded {MaxEventBytes} bytes without terminating.");
            }
        }

        // A final event with no trailing blank line still counts.
        if (data.Length > 0)
        {
            yield return data.ToString();
        }
    }
}
