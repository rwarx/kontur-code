using AIClient.Application.Services;

namespace AIClient.Tests;

/// <summary>
/// The one place a model's output is turned into typed arguments, and the sentences it gets back
/// when it gets that wrong.
/// </summary>
/// <remarks>
/// Every case here is something models actually send: a quoted number, a boolean spelled as a
/// string, an explicit null for an argument they had nothing to say about, a trailing comma. Each
/// one is either accepted or answered with a sentence naming the argument and the expected shape,
/// because the alternative spends a step of the budget on a refusal the model cannot read.
/// </remarks>
public sealed class AgentToolArgumentsTests
{
    [Fact]
    public void Nothing_at_all_is_an_empty_argument_object()
    {
        // Providers differ on what a no-argument call looks like: {}, "" and nothing at all all
        // arrive in practice, and all three mean the same thing.
        foreach (var raw in new[] { null, string.Empty, "   ", "{}" })
        {
            Assert.True(AgentToolArguments.TryParse(raw, out var arguments, out var error), error);
            Assert.False(arguments.TryGetString("path", out _, out var missing));
            Assert.Equal("'path' is required.", missing);
        }
    }

    [Fact]
    public void Text_that_is_not_json_is_refused_with_something_the_model_can_act_on()
    {
        Assert.False(AgentToolArguments.TryParse("{\"path\": \"a.txt\"", out _, out var error));
        Assert.Contains("not valid JSON", error, StringComparison.Ordinal);
        Assert.Contains("again", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_value_that_is_not_an_object_is_refused()
    {
        foreach (var raw in new[] { "[\"a.txt\"]", "\"a.txt\"", "7", "true" })
        {
            Assert.False(AgentToolArguments.TryParse(raw, out _, out var error), raw);
            Assert.Contains("must be a JSON object", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Trailing_commas_and_comments_survive()
    {
        // Not valid JSON by the letter of it, and not worth a refusal either: the argument object
        // was written a token at a time by a model, and both of these are things it does.
        const string raw = """
            {
              // the file to read
              "path": "src/Program.cs",
            }
            """;

        Assert.True(AgentToolArguments.TryParse(raw, out var arguments, out var error), error);
        Assert.True(arguments.TryGetString("path", out var path, out _));
        Assert.Equal("src/Program.cs", path);
    }

    [Fact]
    public void A_string_that_is_missing_wrongly_typed_or_empty_each_explain_themselves()
    {
        Assert.True(AgentToolArguments.TryParse("""{"n": 7, "blank": ""}""", out var arguments, out _));

        Assert.False(arguments.TryGetString("path", out _, out var missing));
        Assert.Equal("'path' is required.", missing);

        Assert.False(arguments.TryGetString("n", out _, out var wrongType));
        Assert.Equal("'n' must be a string.", wrongType);

        Assert.False(arguments.TryGetString("blank", out _, out var empty));
        Assert.Equal("'blank' cannot be empty.", empty);
    }

    [Fact]
    public void An_empty_string_is_accepted_where_emptiness_means_something()
    {
        // write_file with empty content empties the file, which is a real request rather than a
        // forgotten argument. The distinction is the caller's to make, not this type's.
        Assert.True(AgentToolArguments.TryParse("""{"content": ""}""", out var arguments, out _));
        Assert.True(arguments.TryGetString("content", out var content, out var error, allowEmpty: true), error);
        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public void An_explicit_null_is_the_same_as_leaving_it_out()
    {
        // A model with nothing to say for an optional argument often says null rather than omitting
        // it. Treating the two differently would make an optional argument fail once mentioned.
        Assert.True(
            AgentToolArguments.TryParse(
                """{"path": null, "recursive": null, "start_line": null}""",
                out var arguments,
                out _));

        Assert.False(arguments.TryGetString("path", out _, out var missing));
        Assert.Equal("'path' is required.", missing);
        Assert.Null(arguments.GetString("path"));
        Assert.True(arguments.GetBoolean("recursive", fallback: true));
        Assert.True(arguments.TryGetInt32("start_line", out var startLine, out var error), error);
        Assert.Null(startLine);
    }

    [Fact]
    public void A_boolean_sent_as_a_string_is_read_as_one()
    {
        const string raw = """{"a": true, "b": "true", "c": "FALSE", "d": "yes"}""";
        Assert.True(AgentToolArguments.TryParse(raw, out var arguments, out _));

        Assert.True(arguments.GetBoolean("a"));
        Assert.True(arguments.GetBoolean("b"));
        Assert.False(arguments.GetBoolean("c", fallback: true));

        // Not a spelling of a boolean, so the fallback stands rather than a guess being made.
        Assert.True(arguments.GetBoolean("d", fallback: true));
        Assert.False(arguments.GetBoolean("absent"));
    }

    [Fact]
    public void A_whole_number_sent_as_a_string_is_read_as_one()
    {
        Assert.True(AgentToolArguments.TryParse("""{"a": 12, "b": "34"}""", out var arguments, out _));

        Assert.True(arguments.TryGetInt32("a", out var a, out _));
        Assert.Equal(12, a);
        Assert.True(arguments.TryGetInt32("b", out var b, out _));
        Assert.Equal(34, b);
    }

    [Fact]
    public void Something_that_is_not_a_whole_number_is_refused()
    {
        Assert.True(AgentToolArguments.TryParse("""{"a": "twelve", "b": 1.5}""", out var arguments, out _));

        Assert.False(arguments.TryGetInt32("a", out _, out var words));
        Assert.Equal("'a' must be a whole number.", words);

        Assert.False(arguments.TryGetInt32("b", out _, out var fraction));
        Assert.Equal("'b' must be a whole number.", fraction);
    }
}
