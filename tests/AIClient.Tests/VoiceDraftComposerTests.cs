using AIClient.Application.Services;

namespace AIClient.Tests;

/// <summary>
/// How dictation text lands in the composer without eating what was already there.
/// </summary>
/// <remarks>
/// <para>
/// Asserted on the exact text rather than a shape, because the draft is the product: a space
/// missing between what the user typed and what the microphone heard, or a hypothesis that
/// doubles itself on every update, is visible in every message dictated afterwards.
/// </para>
/// <para>
/// The composer is the whole of the voice feature that runs on every event a recognizer
/// raises, which is why it is the part worth pinning down. The engine behind the events
/// varies by machine; the string arithmetic does not.
/// </para>
/// </remarks>
public sealed class VoiceDraftComposerTests
{
    [Fact]
    public void An_empty_draft_receives_the_first_segment_with_no_leading_space()
    {
        var composer = new VoiceDraftComposer("");

        Assert.Equal("hello", composer.AddFinal("hello"));
    }

    [Fact]
    public void A_draft_with_text_receives_the_next_segment_after_one_space()
    {
        var composer = new VoiceDraftComposer("count the");

        Assert.Equal("count the bugs", composer.UpdatePartial("bugs"));
    }

    [Fact]
    public void A_draft_ending_in_a_newline_does_not_gain_a_double_separator()
    {
        var composer = new VoiceDraftComposer("first line\n");

        Assert.Equal("first line\nsecond line", composer.UpdatePartial("second line"));
    }

    [Fact]
    public void A_draft_ending_in_a_space_does_not_gain_a_second_one()
    {
        var composer = new VoiceDraftComposer("count ");

        Assert.Equal("count bugs", composer.UpdatePartial("bugs"));
    }

    [Fact]
    public void Each_hypothesis_replaces_the_previous_one_rather_than_accumulating()
    {
        var composer = new VoiceDraftComposer("");

        composer.UpdatePartial("cou");
        composer.UpdatePartial("coun");
        var draft = composer.UpdatePartial("count");

        Assert.Equal("count", draft);
    }

    [Fact]
    public void A_final_segment_settles_over_the_guess_it_was_refining()
    {
        var composer = new VoiceDraftComposer("");

        composer.UpdatePartial("coun the bugs");
        var draft = composer.AddFinal("count the bugs");

        Assert.Equal("count the bugs", draft);
    }

    [Fact]
    public void A_final_segment_starts_the_next_hypothesis_from_a_clean_slate()
    {
        var composer = new VoiceDraftComposer("");

        composer.AddFinal("first");
        composer.UpdatePartial("seco");

        Assert.Equal("first seco", composer.Compose());
    }

    [Fact]
    public void Consecutive_finals_join_with_single_spaces()
    {
        var composer = new VoiceDraftComposer("");

        composer.AddFinal("one");
        composer.AddFinal("two");

        Assert.Equal("one two", composer.Compose());
    }

    [Fact]
    public void Flushing_commits_the_guess_still_open_so_the_last_sentence_survives()
    {
        var composer = new VoiceDraftComposer("");

        composer.AddFinal("settled sentence.");
        composer.UpdatePartial("last thought");

        Assert.Equal("settled sentence. last thought", composer.Flush());
    }

    [Fact]
    public void Flushing_with_nothing_outstanding_changes_nothing()
    {
        var composer = new VoiceDraftComposer("already here");

        composer.AddFinal("and said");

        Assert.Equal("already here and said", composer.Flush());
    }

    [Fact]
    public void An_empty_final_is_ignored_rather_than_inserted_as_a_gap()
    {
        var composer = new VoiceDraftComposer("before");

        var draft = composer.AddFinal("   ");

        Assert.Equal("before", draft);
    }

    [Fact]
    public void A_null_partial_is_treated_as_silence()
    {
        var composer = new VoiceDraftComposer("");

        composer.UpdatePartial("word");
        var draft = composer.UpdatePartial(null);

        Assert.Equal("", draft);
    }

    [Fact]
    public void Rebasing_onto_an_edited_draft_keeps_the_edited_text_and_drops_the_old_ledger()
    {
        var composer = new VoiceDraftComposer("");

        composer.AddFinal("settled");

        // The user typed between the events; everything composed is inside what they now hold.
        var draft = composer.Rebase("settled and my own words");

        Assert.Equal("settled and my own words", draft);

        composer.AddFinal("more");

        Assert.Equal("settled and my own words more", composer.Compose());
    }

    [Fact]
    public void Rebasing_strips_the_guess_it_was_showing_so_the_next_hypothesis_cannot_double_it()
    {
        var composer = new VoiceDraftComposer("");

        composer.UpdatePartial("guess");

        // The box reads "typed guess"; the user's edit is the exclamation mark, and the guess
        // the engine is still refining is already on screen once.
        var draft = composer.Rebase("typed guess!");

        Assert.Equal("typed guess!", draft);

        composer.UpdatePartial("guess");

        Assert.Equal("typed guess! guess", composer.Compose());
    }

    [Fact]
    public void Rebasing_with_the_composer_s_own_echo_reproduces_it_on_the_next_hypothesis()
    {
        var composer = new VoiceDraftComposer("base");

        var echoed = composer.UpdatePartial("guess");

        composer.Rebase(echoed);
        composer.UpdatePartial("guess");

        Assert.Equal("base guess", composer.Compose());
    }
}
