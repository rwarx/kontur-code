namespace AIClient.Domain.Entities;

/// <summary>
/// Key/value persistence for application settings. A single table with a JSON payload per
/// section keeps schema churn out of migrations as settings evolve.
/// </summary>
public sealed class AppSettingsEntry
{
    /// <summary>Section name, e.g. <c>appearance</c>, <c>chat</c>, <c>general</c>.</summary>
    public required string Key { get; set; }

    /// <summary>Serialized section object.</summary>
    public required string Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
