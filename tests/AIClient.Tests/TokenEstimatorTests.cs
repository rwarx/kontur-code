using AIClient.Application.Services;

namespace AIClient.Tests;

/// <summary>
/// The token heuristic that decides how much history fits in a context window.
/// </summary>
/// <remarks>
/// Asserted as properties rather than exact numbers. The constants are a judgement call and
/// will be tuned; what must not change is the direction of the error. Under-estimating
/// produces an HTTP 400 the user has to recover from, so the estimate is required to come in
/// at or above the conventional four-characters-per-token rule of thumb.
/// </remarks>
public sealed class TokenEstimatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_costs_nothing(string? text)
    {
        Assert.Equal(0, TokenEstimator.Estimate(text));
    }

    [Fact]
    public void Any_text_at_all_costs_at_least_one_token()
    {
        Assert.True(TokenEstimator.Estimate("a") >= 1);
    }

    [Fact]
    public void The_estimate_never_falls_below_four_characters_per_token()
    {
        // The direction that matters: over-estimating trims a turn too many, which is
        // invisible; under-estimating is a rejected request.
        var text = new string('x', 4000);

        Assert.True(
            TokenEstimator.Estimate(text) >= text.Length / 4,
            "The estimate must not be more optimistic than four characters per token.");
    }

    [Fact]
    public void An_English_paragraph_lands_in_a_believable_band()
    {
        const string paragraph =
            "The provider abstraction exists so the chat pipeline never learns which vendor " +
            "answered. Adding a provider is a new class and a registration, not a change to " +
            "the view models.";

        var estimate = TokenEstimator.Estimate(paragraph);

        // Real tokenizers put this text near 35 tokens. A heuristic that came in at 10 or at
        // 300 would be actively harmful to the trimming decision.
        Assert.InRange(estimate, paragraph.Length / 5, paragraph.Length / 2);
    }

    [Fact]
    public void Cyrillic_is_charged_more_per_character_than_Latin()
    {
        // BPE vocabularies are trained mostly on English; Cyrillic fragments into more tokens
        // per character. Treating the two alike would silently overflow a Russian conversation.
        var latin = TokenEstimator.Estimate(new string('a', 100));
        var cyrillic = TokenEstimator.Estimate(new string('щ', 100));

        Assert.True(cyrillic > latin, $"Cyrillic estimated at {cyrillic}, Latin at {latin}.");
    }

    [Fact]
    public void Longer_text_is_never_cheaper()
    {
        var previous = 0;

        foreach (var length in (int[])[1, 10, 100, 1_000, 10_000])
        {
            var estimate = TokenEstimator.Estimate(new string('a', length));

            Assert.True(estimate >= previous, $"{length} characters estimated below {previous}.");
            previous = estimate;
        }
    }

    [Fact]
    public void A_message_costs_its_text_plus_the_chat_template_overhead()
    {
        const string content = "Hello there.";

        Assert.Equal(
            TokenEstimator.Estimate(content) + TokenEstimator.PerMessageOverhead,
            TokenEstimator.EstimateMessage(content));
    }

    [Fact]
    public void An_empty_message_still_costs_its_role_marker()
    {
        // A message with no content is not free on the wire: the role and delimiters are
        // still sent, and forgetting that is how a long history overshoots the window.
        Assert.Equal(TokenEstimator.PerMessageOverhead, TokenEstimator.EstimateMessage(""));
    }
}
