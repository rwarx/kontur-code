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
    /// Reads an optional array of strings: an empty list when it was not sent, and an error when any
    /// element is something other than a string or a number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Numbers are accepted and rendered, because a model writing <c>["-j", 4]</c> means the same thing
    /// as <c>["-j", "4"]</c> and refusing it would spend a step on JSON pedantry. Anything else is an
    /// error: an object or a nested array in an argument list is not a typo for a string, it is a
    /// misunderstanding of what the argument is, and coercing it would send the model's confusion
    /// through to a command line.
    /// </para>
    /// <para>
    /// A bare string is accepted in place of a one-element array for the same reason. What is not
    /// accepted is splitting it - <c>"build --no-restore"</c> stays one argument, because the moment
    /// this method splits on spaces it has become a shell, and a filename with a space in it starts
    /// arriving as two arguments.
    /// </para>
    /// </remarks>
    public bool TryGetStringArray(
        string name,
        out IReadOnlyList<string> values,
        [NotNullWhen(false)] out string? error)
    {
        values = [];
        error = null;

        if (!TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            values = [property.GetString() ?? string.Empty];
            return true;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            error = $"'{name}' must be an array of strings, such as [\"build\", \"--no-restore\"].";
            return false;
        }

        var items = new List<string>(property.GetArrayLength());

        foreach (var element in property.EnumerateArray())
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    items.Add(element.GetString() ?? string.Empty);
                    break;

                case JsonValueKind.Number:
                    items.Add(element.GetRawText());
                    break;

                default:
                    error = $"Every entry in '{name}' has to be a string. Send each argument separately, "
                        + "as its own element.";
                    return false;
            }
        }

        values = items;
        return true;
    }

    /// <summary>
    /// Reads an optional array of objects, each one wrapped so its own fields are read the same way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wrapping rather than deserialising into a record keeps every nested field on the same forgiving
    /// terms as a top-level one - a quoted number is still a number, a missing optional is still absent -
    /// and keeps the error sentences identical, which matters because the model reads them and has no way
    /// to tell how deep the field it got wrong was.
    /// </para>
    /// <para>
    /// A single object is accepted where an array belongs, for the reason a single string is: a model with
    /// one item to send frequently sends the item. Anything that is neither is an error, because an array
    /// of strings where objects belong means the model misread the schema rather than mistyped it.
    /// </para>
    /// </remarks>
    public bool TryGetObjectArray(
        string name,
        out IReadOnlyList<AgentToolArguments> items,
        [NotNullWhen(false)] out string? error)
    {
        items = [];
        error = null;

        if (!TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Object)
        {
            items = [new AgentToolArguments(property.Clone())];
            return true;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            error = $"'{name}' must be an array of objects.";
            return false;
        }

        var read = new List<AgentToolArguments>(property.GetArrayLength());

        foreach (var element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"Every entry in '{name}' has to be an object with its own fields, not a bare value.";
                return false;
            }

            // Cloned per element for the reason the root is: each one outlives this loop, and a
            // JsonElement is a window onto a buffer rather than a copy of it.
            read.Add(new AgentToolArguments(element.Clone()));
        }

        items = read;
        return true;
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
