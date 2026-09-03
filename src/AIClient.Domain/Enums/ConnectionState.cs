namespace AIClient.Domain.Enums;

/// <summary>Result of probing a provider with the user's current credentials.</summary>
public enum ConnectionState
{
    /// <summary>No API key stored.</summary>
    NotConfigured = 0,

    /// <summary>A key exists but has not been probed in this session.</summary>
    Unknown = 1,

    /// <summary>A probe is in flight.</summary>
    Testing = 2,

    /// <summary>The last probe succeeded.</summary>
    Connected = 3,

    /// <summary>The last probe failed. The accompanying message says why.</summary>
    Failed = 4,
}
