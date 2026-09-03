using AIClient.Infrastructure.Http;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The SSE framing that section 6's token-by-token streaming rests on.
/// </summary>
/// <remarks>
/// Worth testing on its own rather than only through a provider, because the failure this
/// reader exists to prevent is invisible from above: a chunk boundary that lands mid-line
/// silently drops a token, and the answer is subtly truncated instead of obviously broken.
/// Every test here that passes a chunk size is testing exactly that.
/// </remarks>
public sealed class ServerSentEventReaderTests
{
    [Fact]
    public async Task Each_blank_line_terminated_frame_is_one_event()
    {
        var events = await ReadAllAsync("data: one\n\ndata: two\n\ndata: three\n\n");

        Assert.Equal(["one", "two", "three"], events);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    public async Task A_frame_split_across_reads_is_reassembled(int chunkSize)
    {
        // The whole point of the class. At a chunk size of 1 every read ends mid-line.
        var events = await ReadAllAsync(WireFixtures.ChatStream, chunkSize);

        Assert.Contains("[DONE]", events);
        Assert.Contains(events, e => e.Contains("\"content\":\", world\"", StringComparison.Ordinal));
        Assert.Equal(5, events.Count);
    }

    [Fact]
    public async Task Comment_keep_alives_carry_no_payload_and_produce_no_event()
    {
        // OpenRouter sends ": OPENROUTER PROCESSING" while an upstream model warms up.
        var events = await ReadAllAsync(": OPENROUTER PROCESSING\n\ndata: real\n\n");

        Assert.Equal(["real"], events);
    }

    [Fact]
    public async Task Fields_other_than_data_are_ignored()
    {
        // No provider the app talks to uses them, and treating "event: message" as a payload
        // would hand the JSON parser a line of framing.
        var events = await ReadAllAsync("event: message\nid: 42\nretry: 1000\ndata: payload\n\n");

        Assert.Equal(["payload"], events);
    }

    [Fact]
    public async Task Exactly_one_space_after_the_colon_belongs_to_the_framing()
    {
        // "data:  x" carries a leading space that is part of the value. Trimming it would
        // eat a real space out of the middle of a streamed word.
        var events = await ReadAllAsync("data:  x\n\ndata:y\n\n");

        Assert.Equal([" x", "y"], events);
    }

    [Fact]
    public async Task Several_data_lines_in_one_frame_join_with_a_newline()
    {
        var events = await ReadAllAsync("data: first\ndata: second\n\n");

        Assert.Equal(["first\nsecond"], events);
    }

    [Fact]
    public async Task A_final_frame_with_no_trailing_blank_line_is_not_lost()
    {
        // A provider that closes the connection straight after the last token would
        // otherwise cost the user the end of the answer.
        var events = await ReadAllAsync("data: one\n\ndata: last");

        Assert.Equal(["one", "last"], events);
    }

    [Fact]
    public async Task Windows_line_endings_are_read_the_same_as_unix_ones()
    {
        // Proxies and gateways normalise to CRLF; a reader that only splits on \n would
        // leave a stray \r on the end of every JSON frame.
        var events = await ReadAllAsync("data: one\r\n\r\ndata: two\r\n\r\n");

        Assert.Equal(["one", "two"], events);
    }

    [Fact]
    public async Task An_empty_data_line_produces_nothing_to_hand_the_parser()
    {
        var events = await ReadAllAsync("data:\n\ndata: real\n\n");

        Assert.Equal(["real"], events);
    }

    [Fact]
    public async Task An_empty_body_ends_without_an_event()
    {
        Assert.Empty(await ReadAllAsync(string.Empty));
    }

    [Fact]
    public async Task A_frame_that_never_terminates_is_refused_rather_than_buffered_forever()
    {
        // A provider stuck mid-frame must not be able to grow the buffer until the process
        // dies. The cap is far above any real payload.
        var runaway = "data: " + new string('a', (4 * 1024 * 1024) + 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(runaway));
    }

    [Fact]
    public async Task A_token_cancelled_before_the_first_read_yields_nothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAllAsync(WireFixtures.ChatStream, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Cancelling_part_way_through_stops_the_read_instead_of_draining_it()
    {
        // Section 22. Stop has to abort the response, not politely finish consuming it.
        using var cts = new CancellationTokenSource();
        var stream = new ChunkedStream(WireFixtures.SplitEvery(WireFixtures.ChatStream, 16));
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in ServerSentEventReader.ReadAsync(stream, cts.Token))
            {
                seen++;
                await cts.CancelAsync();
            }
        });

        Assert.Equal(1, seen);
    }

    private static async Task<List<string>> ReadAllAsync(
        string body,
        int chunkSize = 0,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<string> chunks = chunkSize > 0
            ? WireFixtures.SplitEvery(body, chunkSize)
            : [body];

        var events = new List<string>();

        await foreach (var data in ServerSentEventReader.ReadAsync(new ChunkedStream(chunks), cancellationToken))
        {
            events.Add(data);
        }

        return events;
    }
}
