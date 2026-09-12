namespace AIClient.Application.Services;

/// <summary>
/// Folds dictation into the draft: whatever the user already had in the box, the segments the
/// engine has settled, and the guess it is still refining.
/// </summary>
/// <remarks>
/// <para>
/// A plain class rather than logic spread over the ViewModel, because its whole job is a small
/// state machine over strings - the kind of thing unit tests can pin down, which a
/// dispatcher-bound ViewModel cannot. The ViewModel drives it and writes the result into the
/// draft; nothing here knows WPF exists.
/// </para>
/// <para>
/// The engine's rhythm maps onto three calls: <see cref="UpdatePartial"/> as the guess
/// improves, <see cref="AddFinal"/> when a segment settles, and <see cref="Flush"/> when the
/// session ends with a guess still outstanding. <see cref="Rebase"/> covers the one thing the
/// other three cannot express: the user typed into the box while dictation was running, and
/// every word in it is now theirs again.
/// </para>
/// </remarks>
public sealed class VoiceDraftComposer
{
    /// <summary>What the box held when dictation began, or when the user last typed.</summary>
    private string _baseText;

    /// <summary>Segments the engine has settled and the draft has not absorbed yet.</summary>
    private string _committed = string.Empty;

    /// <summary>The guess the engine is still refining; replaced on every hypothesis.</summary>
    private string _pending = string.Empty;

    /// <summary>Opens dictation over the draft as it stands: the words arrive after it, not over it.</summary>
    /// <remarks>
    /// Trailing whitespace is left exactly as the user left it. <see cref="Join"/> already
    /// declines to double a space or a newline that is there, so trimming here would only
    /// destroy information - a newline before the dictated words is a paragraph break the
    /// user asked for.
    /// </remarks>
    public VoiceDraftComposer(string baseText)
    {
        _baseText = baseText;
    }

    /// <summary>Replaces the guess still being refined. Composing twice with the same guess is harmless.</summary>
    public string UpdatePartial(string? text)
    {
        _pending = (text ?? string.Empty).Trim();
        return Compose();
    }

    /// <summary>Commits a settled segment and clears the guess it was refining.</summary>
    public string AddFinal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Compose();
        }

        _committed = Join(_committed, text.Trim());
        _pending = string.Empty;
        return Compose();
    }

    /// <summary>
    /// Ends the session by committing the guess still open, if any.
    /// </summary>
    /// <remarks>
    /// The guess is what the user has been reading in the box while it was being refined, and
    /// it is usually the last sentence they spoke - throwing it away to honour a distinction
    /// between "settled" and "nearly settled" that nobody listening can hear would drop it.
    /// </remarks>
    public string Flush()
    {
        if (_pending.Length > 0)
        {
            _committed = Join(_committed, _pending);
            _pending = string.Empty;
        }

        return Compose();
    }

    /// <summary>
    /// Hands the running guess a new base, after the user typed into the draft.
    /// </summary>
    /// <remarks>
    /// Everything already composed is inside the draft the user just edited, so it is folded
    /// back into the base and the ledger starts over. The guess still being heard is stripped
    /// from the end of the draft first, so the next hypothesis cannot double the words that
    /// are already there.
    /// </remarks>
    public string Rebase(string draft)
    {
        _baseText = RemoveEnding(draft, _pending);
        _committed = string.Empty;
        _pending = string.Empty;
        return Compose();
    }

    /// <summary>The draft as it should read right now.</summary>
    public string Compose() => Join(Join(_baseText, _committed), _pending);

    /// <summary>
    /// Joins two stretches of text the way a person would type them: a single space between
    /// words, nothing added when a newline or a space is already there.
    /// </summary>
    private static string Join(string left, string right)
    {
        if (right.Length == 0)
        {
            return left;
        }

        if (left.Length == 0)
        {
            return right;
        }

        // A newline on either side is its own separator, and a space already present is not
        // doubled: dictation appends to a sentence, it does not re-typeset it.
        return char.IsWhiteSpace(left[^1]) || char.IsWhiteSpace(right[0])
            ? left + right
            : left + " " + right;
    }

    /// <summary>Cuts a trailing occurrence of <paramref name="ending"/> off <paramref name="text"/>.</summary>
    /// <remarks>
    /// Tolerates the separator space the composer itself inserted, because that space is part
    /// of the composition and not of what the user wrote. An ending that is not at the end -
    /// the user deleted or rewrote it - is simply left alone.
    /// </remarks>
    private static string RemoveEnding(string text, string ending)
    {
        if (ending.Length == 0)
        {
            return text;
        }

        foreach (var candidate in new[] { ending, ending + " " })
        {
            if (text.EndsWith(candidate, StringComparison.Ordinal))
            {
                return text[..^candidate.Length];
            }
        }

        return text;
    }
}
