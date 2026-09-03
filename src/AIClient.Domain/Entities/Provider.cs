namespace AIClient.Domain.Entities;

/// <summary>
/// A configured AI backend (OpenRouter, NVIDIA, and later Ollama/LM Studio/...).
/// The row exists as soon as the provider is known to the app; <see cref="IsEnabled"/>
/// and the presence of a key in secure storage decide whether it is usable.
/// </summary>
/// <remarks>
/// The API key is deliberately NOT a property of this entity: it lives in
/// <c>ISecureStorage</c> (DPAPI-encrypted), keyed by <see cref="Id"/>. Nothing that
/// gets written to the SQLite file can leak a credential.
/// </remarks>
public sealed class Provider
{
    /// <summary>Stable identifier, e.g. <c>openrouter</c>. Also the secure-storage key and the FK used by models.</summary>
    public required string Id { get; set; }

    /// <summary>Human name shown in Settings, e.g. "OpenRouter".</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Wire protocol family. Currently always <c>openai-compatible</c>; exists so a future
    /// Anthropic-native or Ollama-native provider can be told apart without a schema change.
    /// </summary>
    public string Type { get; set; } = "openai-compatible";

    /// <summary>User-overridable base URL. Null means "use the provider's built-in default".</summary>
    public string? BaseUrlOverride { get; set; }

    /// <summary>When false the provider is hidden from the model picker and never contacted.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Ordering hint for Settings and the grouped model list.</summary>
    public int SortOrder { get; set; }

    /// <summary>When the model catalogue was last fetched. Null means never.</summary>
    public DateTimeOffset? ModelsRefreshedAt { get; set; }

    public ICollection<Model> Models { get; set; } = [];
}
