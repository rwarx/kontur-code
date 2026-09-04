using System.Text;
using AIClient.Domain.Models;

namespace AIClient.Infrastructure.Providers.OpenAiCompatible;

/// <summary>
/// Reassembles the tool calls of one streamed response from the fragments the protocol sends
/// them in.
/// </summary>
/// <remarks>
/// <para>
/// A streamed tool call arrives as a name and an id in one frame, then the argument JSON a few
/// characters at a time across as many frames as it takes - and when a model calls two tools in
/// one turn, both are interleaved, distinguished only by <c>index</c>. Nothing downstream can act
/// on a fragment, so the joining happens here, at the edge that knows the wire format, and the
/// rest of the application only ever sees whole <see cref="AIToolCall"/> values.
/// </para>
/// <para>
/// Two defences against a provider that does not follow the specification exactly:
/// <c>index</c> is corroborated with <c>id</c>, because a frame that omits the index deserialises
/// to 0 and would otherwise append a second call's arguments onto the first; and the arguments
/// are capped, because the only thing bounding them otherwise is the model's willingness to stop
/// emitting characters.
/// </para>
/// </remarks>
internal sealed class ToolCallAccumulator
{
    /// <summary>
    /// Cap on one call's argument text. Well beyond any real argument object - the largest tool
    /// here takes a file's full contents, and the workspace refuses files far smaller than this -
    /// while still bounding a model that has started repeating itself.
    /// </summary>
    /// <remarks>
    /// Truncating leaves invalid JSON on purpose. The agent reports an unparseable argument
    /// object back to the model as a tool result, so a runaway call costs one wasted step and the
    /// model gets to correct it, whereas throwing here would end the turn with nothing to show.
    /// </remarks>
    private const int MaxArgumentsLength = 256 * 1024;

    private readonly List<Slot> _slots = [];

    /// <summary>Calls that arrived without a usable name and were dropped.</summary>
    public int DiscardedCount { get; private set; }

    /// <summary>Calls whose arguments hit <see cref="MaxArgumentsLength"/> and were cut short.</summary>
    public int TruncatedCount { get; private set; }

    public bool HasCalls => _slots.Count > 0;

    /// <summary>
    /// Folds one frame's <c>tool_calls</c> array in, and returns the progress events to surface
    /// for it - one per fragment, in arrival order.
    /// </summary>
    public List<AIStreamEvent.ToolCallDelta> Add(IReadOnlyList<OpenAiWire.ToolCallChunk> chunks)
    {
        var events = new List<AIStreamEvent.ToolCallDelta>(chunks.Count);

        foreach (var chunk in chunks)
        {
            var slot = Resolve(chunk);

            if (!string.IsNullOrEmpty(chunk.Id))
            {
                slot.Id = chunk.Id;
            }

            if (!string.IsNullOrEmpty(chunk.Function?.Name))
            {
                // Concatenated rather than assigned: a handful of gateways split even the name.
                slot.Name.Append(chunk.Function.Name);
            }

            var fragment = chunk.Function?.Arguments;
            if (!string.IsNullOrEmpty(fragment))
            {
                Append(slot, fragment);
            }

            events.Add(new AIStreamEvent.ToolCallDelta(
                slot.Ordinal,
                chunk.Id,
                chunk.Function?.Name,
                fragment));
        }

        return events;
    }

    /// <summary>
    /// The finished calls, in the order the model asked for them. Anonymous calls are dropped:
    /// a call with no name cannot be dispatched, and inventing one would run the wrong tool.
    /// </summary>
    public IReadOnlyList<AIToolCall> Build()
    {
        var calls = new List<AIToolCall>(_slots.Count);

        foreach (var slot in _slots)
        {
            var name = slot.Name.ToString();

            if (string.IsNullOrWhiteSpace(name))
            {
                DiscardedCount++;
                continue;
            }

            // A missing id is synthesised rather than treated as fatal. The id only has to be
            // stable enough to pair this call with the tool message answering it, and a provider
            // that did not issue one has nothing to compare ours against.
            var id = string.IsNullOrEmpty(slot.Id) ? $"call_{slot.Ordinal}" : slot.Id;

            calls.Add(new AIToolCall(id, name, slot.Arguments.ToString()));
        }

        return calls;
    }

    /// <summary>
    /// Finds the call a fragment belongs to, or starts a new one.
    /// </summary>
    /// <remarks>
    /// The index is trusted first, since that is what the protocol says identifies a call. It is
    /// then checked against the id: a fragment carrying an id that disagrees with the one already
    /// in that slot cannot belong to it, and the alternative - appending it anyway - produces a
    /// single call with two names' worth of arguments spliced together.
    /// </remarks>
    private Slot Resolve(OpenAiWire.ToolCallChunk chunk)
    {
        foreach (var candidate in _slots)
        {
            if (candidate.WireIndex != chunk.Index)
            {
                continue;
            }

            var mismatch = !string.IsNullOrEmpty(chunk.Id)
                && !string.IsNullOrEmpty(candidate.Id)
                && !string.Equals(candidate.Id, chunk.Id, StringComparison.Ordinal);

            if (mismatch)
            {
                break;
            }

            return candidate;
        }

        var slot = new Slot(chunk.Index, _slots.Count);
        _slots.Add(slot);

        return slot;
    }

    private void Append(Slot slot, string fragment)
    {
        if (slot.Truncated)
        {
            return;
        }

        var room = MaxArgumentsLength - slot.Arguments.Length;

        if (fragment.Length <= room)
        {
            slot.Arguments.Append(fragment);
            return;
        }

        slot.Arguments.Append(fragment.AsSpan(0, Math.Max(room, 0)));
        slot.Truncated = true;
        TruncatedCount++;
    }

    /// <param name="WireIndex">The <c>index</c> the provider sent, used to join fragments.</param>
    /// <param name="Ordinal">Position among the calls of this turn, which is what callers see.</param>
    private sealed record Slot(int WireIndex, int Ordinal)
    {
        public string? Id { get; set; }

        public StringBuilder Name { get; } = new();

        public StringBuilder Arguments { get; } = new();

        public bool Truncated { get; set; }
    }
}
