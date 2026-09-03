using AIClient.Application.Services;

namespace AIClient.Tests;

/// <summary>
/// Automatic chat titles (section 16), derived locally from the first user message.
/// </summary>
/// <remarks>
/// A local heuristic rather than a model call: naming a chat is not worth a billed request,
/// a round trip before the sidebar can update, or a title that fails to appear when the key
/// is wrong. The trade-off is that the algorithm has to be predictable, which is what these
/// tests pin down.
/// </remarks>
public sealed class TitleGeneratorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void Nothing_usable_produces_no_title(string? input)
    {
        // Null rather than a placeholder: the caller leaves the existing title alone.
        Assert.Null(HeuristicTitleGenerator.Generate(input));
    }

    [Fact]
    public void A_leading_verb_and_filler_word_are_dropped()
    {
        // "Explain this X" and "X" are the same chat. The imperative adds nothing to a
        // sidebar where every row is a request.
        Assert.Equal(
            "WPF binding error, please",
            HeuristicTitleGenerator.Generate("Explain this WPF binding error, please."));
    }

    [Fact]
    public void Only_the_first_sentence_is_used()
    {
        Assert.Equal(
            "Two problems here",
            HeuristicTitleGenerator.Generate("Two problems here. The second one is worse. Also a third."));
    }

    [Fact]
    public void A_decimal_point_does_not_end_the_sentence()
    {
        // The terminator has to be followed by whitespace, or "3.5" truncates the title.
        Assert.Equal(
            "Version 3.5 broke the build",
            HeuristicTitleGenerator.Generate("Version 3.5 broke the build"));
    }

    [Fact]
    public void A_fenced_code_block_is_not_the_title()
    {
        // Paste-first is the common case: the question sits underneath the code.
        var title = HeuristicTitleGenerator.Generate(
            """
            ```csharp
            var x = client.GetAsync(url).Result;
            ```
            What is wrong with this?
            """);

        Assert.Equal("Wrong with this", title);
    }

    [Fact]
    public void Markdown_decoration_is_stripped()
    {
        Assert.Equal("Deadlock in the sync path", HeuristicTitleGenerator.Generate("## `Deadlock` in the sync path"));
    }

    [Fact]
    public void A_Russian_imperative_is_stripped_too()
    {
        // The user's own spec is written in Russian, so the app is going to be prompted in it.
        Assert.Equal(
            "Функцию для парсинга JSON",
            HeuristicTitleGenerator.Generate("Напиши функцию для парсинга JSON"));
    }

    [Fact]
    public void Stripping_that_would_empty_the_title_is_undone()
    {
        // "Explain this" is nothing but a verb and a filler. Dropping both leaves an empty
        // string, so the original wins - an unhelpful title beats a blank sidebar row.
        Assert.Equal("Explain this", HeuristicTitleGenerator.Generate("explain this"));
    }

    [Fact]
    public void A_long_title_is_cut_at_a_word_boundary()
    {
        var title = HeuristicTitleGenerator.Generate(
            "Write a function that validates an email address and returns a boolean");

        Assert.NotNull(title);
        Assert.StartsWith("Function that validates", title, StringComparison.Ordinal);
        Assert.EndsWith("…", title, StringComparison.Ordinal);

        // Mid-word truncation looks like a rendering bug rather than an abbreviation.
        Assert.DoesNotContain(" …", title, StringComparison.Ordinal);
        Assert.True(title.Length <= 49, $"Title was {title.Length} characters: '{title}'.");
    }

    [Fact]
    public void A_single_word_longer_than_the_limit_is_still_truncated()
    {
        // No space to cut at. The result has to be bounded anyway.
        var title = HeuristicTitleGenerator.Generate(new string('a', 200));

        Assert.NotNull(title);
        Assert.True(title.Length <= 49, $"Title was {title.Length} characters.");
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_letter_is_capitalised()
    {
        Assert.Equal("Slow startup on cold boot", HeuristicTitleGenerator.Generate("slow startup on cold boot"));
    }

    [Fact]
    public void An_acronym_keeps_its_case()
    {
        // Capitalising the first letter must not lower-case anything else.
        Assert.Equal("HTTP 429 from OpenRouter", HeuristicTitleGenerator.Generate("HTTP 429 from OpenRouter"));
    }

    [Fact]
    public void A_verb_is_only_stripped_at_a_word_boundary()
    {
        // "Helpers" starts with "help". Prefix matching without a boundary check would
        // turn this into "ers for the parser".
        Assert.Equal("Helpers for the parser", HeuristicTitleGenerator.Generate("Helpers for the parser"));
    }

    [Fact]
    public async Task The_async_entry_point_agrees_with_the_synchronous_core()
    {
        const string message = "How do I cancel a streaming request?";

        Assert.Equal(
            HeuristicTitleGenerator.Generate(message),
            await new HeuristicTitleGenerator().GenerateAsync(message));
    }
}
