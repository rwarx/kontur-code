namespace AIClient.Application.Markdown;

/// <summary>Semantic class of a code token. The view maps these to brushes per theme.</summary>
public enum CodeTokenKind
{
    /// <summary>Anything not otherwise classified: identifiers, operators, whitespace.</summary>
    Plain = 0,
    Keyword,
    Type,
    String,
    Number,
    Comment,
    /// <summary>Preprocessor directives, decorators, annotations, shell flags.</summary>
    Directive,
    /// <summary>An identifier immediately followed by an opening parenthesis.</summary>
    Function,
}

/// <summary>A classified run of source text. Runs tile a line with no gaps.</summary>
public readonly record struct CodeToken(string Text, CodeTokenKind Kind);
