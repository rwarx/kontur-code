using AIClient.Application.Services;

namespace AIClient.Tests;

/// <summary>
/// The diff a user reads before approving a change.
/// </summary>
/// <remarks>
/// <para>
/// Asserted on the exact text rather than on a shape, because the exact text is the product. A diff whose
/// hunk header is off by one, or which reports a line as changed when only its line ending moved, is worse
/// than no diff: it spends the user's trust on a claim about their files that is not true.
/// </para>
/// <para>
/// The bounds are tested as carefully as the output. Both exist to keep an approval dialog from hanging or
/// from printing a file twice, and neither is exercised by an ordinary edit, so nothing but a test will
/// ever notice if one breaks.
/// </para>
/// </remarks>
public sealed class TextDiffTests
{
    [Fact]
    public void Two_texts_that_say_the_same_thing_have_no_diff()
    {
        Assert.Null(TextDiff.Unified("one\ntwo\n", "one\ntwo\n"));
    }

    [Fact]
    public void A_rewritten_line_shows_as_a_removal_and_an_addition_between_its_neighbours()
    {
        var diff = TextDiff.Unified("one\ntwo\nthree\n", "one\n2\nthree\n", "a.txt");

        Assert.Equal(
            """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             one
            -two
            +2
             three
            """.ReplaceLineEndings("\n"),
            diff);
    }

    [Fact]
    public void A_new_file_is_every_line_added_and_says_so_in_the_header()
    {
        var diff = TextDiff.Unified(null, "alpha\nbeta\n");

        Assert.Equal("@@ -0,0 +1,2 @@\n+alpha\n+beta", diff);
    }

    [Fact]
    public void A_file_being_emptied_is_every_line_removed()
    {
        var diff = TextDiff.Unified("alpha\nbeta\n", null);

        Assert.Equal("@@ -1,2 +0,0 @@\n-alpha\n-beta", diff);
    }

    [Fact]
    public void A_line_ending_is_not_a_change()
    {
        Assert.Null(TextDiff.Unified("one\r\ntwo\r\n", "one\ntwo\n"));
        Assert.Null(TextDiff.Unified("one\rtwo", "one\ntwo"));
    }

    [Fact]
    public void A_missing_final_newline_is_not_a_change()
    {
        Assert.Null(TextDiff.Unified("one\ntwo", "one\ntwo\n"));
    }

    [Fact]
    public void Only_the_lines_around_a_change_are_shown()
    {
        var before = Numbered(1, 20);
        var diff = TextDiff.Unified(before, before.Replace("line 2\n", "changed\n", StringComparison.Ordinal));

        Assert.NotNull(diff);

        // Three either side, and none of the seventeen lines the change says nothing about.
        Assert.Contains(" line 5", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("line 6", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void Changes_far_apart_are_two_hunks_and_changes_close_together_are_one()
    {
        var before = Numbered(1, 20);

        var apart = TextDiff.Unified(
            before,
            before
                .Replace("line 2\n", "changed\n", StringComparison.Ordinal)
                .Replace("line 18\n", "changed too\n", StringComparison.Ordinal));

        var together = TextDiff.Unified(
            before,
            before
                .Replace("line 2\n", "changed\n", StringComparison.Ordinal)
                .Replace("line 6\n", "changed too\n", StringComparison.Ordinal));

        Assert.Equal(2, Hunks(apart));
        Assert.Equal(1, Hunks(together));
    }

    [Fact]
    public void A_diff_longer_than_the_cap_stops_and_says_that_it_did()
    {
        var diff = TextDiff.Unified(Numbered(1, 200), Numbered(500, 200), maxLines: 20);

        Assert.NotNull(diff);
        Assert.EndsWith(TextDiff.TruncationNotice, diff, StringComparison.Ordinal);
        Assert.True(diff.Split('\n').Length <= 21, diff.Split('\n').Length.ToString());
    }

    /// <summary>
    /// Two texts too large to compare line by line are reported as one block replacing another.
    /// </summary>
    /// <remarks>
    /// The pair here differs only in its first and last line, so a line-by-line comparison would show
    /// twelve hundred lines of context. Its absence is what proves the budget was enforced - and the
    /// header still has to be right, because that is all the user has left to read.
    /// </remarks>
    [Fact]
    public void A_pair_too_large_to_compare_degrades_to_a_block_replacement()
    {
        var lines = Enumerable.Range(1, 1200).Select(i => $"line {i}").ToArray();
        var before = string.Join('\n', lines) + "\n";

        lines[0] = "first";
        lines[^1] = "last";

        var diff = TextDiff.Unified(before, string.Join('\n', lines) + "\n");

        Assert.NotNull(diff);
        Assert.StartsWith("@@ -1,1200 +1,1200 @@", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("\n ", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_are_counted_the_way_the_diff_counts_them()
    {
        Assert.Equal(0, TextDiff.CountLines(null));
        Assert.Equal(0, TextDiff.CountLines(string.Empty));
        Assert.Equal(1, TextDiff.CountLines("one"));
        Assert.Equal(1, TextDiff.CountLines("one\n"));
        Assert.Equal(2, TextDiff.CountLines("one\ntwo"));
        Assert.Equal(2, TextDiff.CountLines("one\r\ntwo\r\n"));
        Assert.Equal(3, TextDiff.CountLines("one\n\ntwo\n"));
    }

    private static string Numbered(int first, int count) =>
        string.Concat(Enumerable.Range(first, count).Select(i => $"line {i}\n"));

    private static int Hunks(string? diff) =>
        diff is null ? 0 : diff.Split('\n').Count(line => line.StartsWith("@@", StringComparison.Ordinal));
}
