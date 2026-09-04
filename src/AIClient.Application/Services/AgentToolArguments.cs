using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AIClient.Application.Services;

/// <summary>
/// The arguments of one tool call, read out by name.
/// </summary>
/// <remarks>
/// <para>
/// Arrives as a string of JSON the model wrote a character at a time, so it is parsed once here and
/// every accessor answers with either a value or a sentence explaining what is missing. That sentence
/// goes back as the tool result: <c>'path' is required</c> is something a model can act on, where a
/// <see cref="JsonException"/> stack is not.
/// </para>
/// <para>
/// Deliberately forgiving in two places, both of which are things models genuinely do. A number sent
/// as a quoted string is accepted, and so is a boolean; the alternative is spending a step of the
/// budget on a refusal over quotation marks. Nothing else is coerced - a string where an object
/// belongs is still an error, because guessing there would mean guessing at intent.
/// </para>
/// </remarks>
public sealed class AgentToolArguments
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly JsonElement _root;

    private AgentToolArguments(JsonElement root) => _root = root;

    /// <summary>No arguments at all, for a tool that takes none.</summary>
    public static AgentToolArguments Empty { get; } = new(default);

    /// <summary>
    /// Reads the argument object, or explains why it cannot be read.
    /// </summary>
    /// <remarks>
    /// Empty text is an empty object rather than an error. A model calling a no-argument tool sends
    /// <c>{}</c>, <c>""</c> or nothing at all depending on the provider, and all three mean the same
    /// thing.
    /// </remarks>
    public static bool TryParse(
        string? raw,
        [NotNullWhen(true)] out AgentToolArguments? arguments,
        [NotNullWhen(false)] out string? error)
    {
        arguments = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            arguments = Empty;
            error = null;
            return true;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(raw, ParseOptions);
        }
        catch (JsonException ex)
        {
            error = $"The arguments are not valid JSON ({ex.Message}). Send the whole argument object again.";
            return false;
        }

        // Cloned so the value survives the document being disposed: JsonElement is a window onto
        // the document's buffer, and a tool reads its arguments long after this method returns.
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The arguments must be a JSON object, such as {\"path\": \"src/Program.cs\"}.";
                return false;
            }

            arguments = new AgentToolArguments(document.RootElement.Clone());
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Reads a required string.
    /// </summary>
    /// <param name="allowEmpty">
    /// True where an empty string is a real answer rather than a mistake - the contents of a file being
    /// emptied, for instance, which is a legitimate write and must not be refused as a missing argument.
    /// </param>
    public bool TryGetString(
        string name,
        [NotNullWhen(true)] out string? value,
        [NotNullWhen(false)] out string? error,
        bool allowEmpty = false)
    {
        value = null;

        if (!TryGetProperty(name, out var property))
        {
            error = $"'{name}' is required.";
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            error = $"'{name}' must be a string.";
            return false;
        }

        value = property.GetString() ?? string.Empty;

        if (value.Length == 0 && !allowEmpty)
        {
            error = $"'{name}' cannot be empty.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Reads an optional string, which is null when it was not sent.</summary>
    public string? GetString(string name) =>
        TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>Reads an optional boolean, falling back when it was not sent.</summary>
    public bool GetBoolean(string name, bool fallback = false)
    {
        if (!TryGetProperty(name, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>
    /// Reads an optional whole number: null when it was not sent, and an error when it was sent as
    /// something that is not one.
    /// </summary>
    public bool TryGetInt32(string name, out int? value, [NotNullWhen(false)] out string? error)
    {
        value = null;
        error = null;

        if (!TryGetProperty(name, out var property))
        {
            return true;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt32(out var number):
                value = number;
                return true;

            case JsonValueKind.String when int.TryParse(property.GetString(), out var parsed):
                value = parsed;
                return true;

            default:
                error = $"'{name}' must be a whole number.";
                return false;
        }
    }

    /// <summary>
    /// Finds a property, and treats an explicit null as absent.
    /// </summary>
    /// <remarks>
    /// A model that has nothing to say for an optional argument often says <c>null</c> rather than
    /// leaving it out. Both mean the same thing, and treating them differently would make an
    /// optional argument fail once it had been mentioned.
    /// </remarks>
    private bool TryGetProperty(string name, out JsonElement property)
    {
        property = default;

        if (_root.ValueKind != JsonValueKind.Object || !_root.TryGetProperty(name, out var found))
        {
            return false;
        }

        property = found;
        return found.ValueKind != JsonValueKind.Null;
    }
}
