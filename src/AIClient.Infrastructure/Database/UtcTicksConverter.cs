using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AIClient.Infrastructure.Database;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as UTC ticks in an INTEGER column.
/// </summary>
/// <remarks>
/// SQLite has no date type. Left to itself, the provider maps <see cref="DateTimeOffset"/>
/// to TEXT of the form <c>yyyy-MM-dd HH:mm:ss.FFFFFFFzzz</c>, and then refuses to translate
/// <c>ORDER BY</c>, <c>MIN</c>, <c>MAX</c> or a range comparison over it - because two rows
/// written in different time zones would sort by their local wall clock rather than by the
/// instant they describe. That is a real hazard, not a provider quirk, so the fix is to store
/// something genuinely ordered rather than to sort on the client.
///
/// UTC ticks are exact, fixed-width and monotonic, which makes the composite index on
/// (IsPinned, UpdatedAt) usable for the sidebar's ordering instead of decorative.
///
/// The original offset is not preserved: every timestamp in this application is created with
/// <see cref="DateTimeOffset.UtcNow"/>, so there is no offset to lose. Values come back with
/// <see cref="TimeSpan.Zero"/>, which is what was written.
/// </remarks>
public sealed class UtcTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public UtcTicksConverter()
        : base(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
    {
    }
}
