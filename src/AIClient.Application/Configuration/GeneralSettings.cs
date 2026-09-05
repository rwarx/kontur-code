namespace AIClient.Application.Configuration;

/// <summary>
/// Which language the user interface is written in. English is the language the
/// application was built in and the default; Russian and German are full translations.
/// </summary>
public enum UiLanguage
{
    English = 0,
    Russian = 1,
    German = 2,
}

/// <summary>Application-wide behaviour that does not belong to a more specific section.</summary>
public sealed class GeneralSettings
{
    /// <summary>False until the first-run wizard has been completed or skipped.</summary>
    public bool HasCompletedFirstRun { get; set; }

    /// <summary>Restore the last open conversation when the app starts.</summary>
    public bool RestoreLastConversation { get; set; } = true;

    /// <summary>Ask before deleting a conversation.</summary>
    public bool ConfirmBeforeDelete { get; set; } = true;

    /// <summary>Derive a chat title from the first user message.</summary>
    public bool AutoGenerateTitles { get; set; } = true;

    /// <summary>Language of the user interface. Applied without a restart.</summary>
    public UiLanguage Language { get; set; } = UiLanguage.English;

    /// <summary>Conversation open at shutdown, so it can be reopened. Null when none.</summary>
    public Guid? LastConversationId { get; set; }
}
