namespace AIClient.Domain.Graph;

/// <summary>
/// The one place that decides how a kind name is written down.
/// </summary>
/// <remarks>
/// Both <see cref="GraphNodeKind"/> and <see cref="GraphEdgeKind"/> are persisted as text and
/// compared by value, so they have to agree on casing and trimming exactly. Two copies of that
/// rule is two copies that can drift, and the drift would show up as a duplicate node rather
/// than as a compile error.
/// </remarks>
internal static class GraphKindText
{
    /// <summary>The value both kinds use when nothing meaningful was supplied.</summary>
    internal const string Unknown = "unknown";

    /// <summary>
    /// Canonicalises a kind name, returning null for the unknown case.
    /// </summary>
    /// <remarks>
    /// Null rather than <see cref="Unknown"/> so that the unknown kind is representable as
    /// <c>default</c>. A struct whose default value is not equal to any of its named values is a
    /// trap: <c>default(GraphNodeKind) == GraphNodeKind.Unknown</c> has to hold, and the cheapest
    /// way to guarantee it is to store the unknown case as an absent value.
    /// </remarks>
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToLowerInvariant();

        return trimmed == Unknown ? null : trimmed;
    }
}
