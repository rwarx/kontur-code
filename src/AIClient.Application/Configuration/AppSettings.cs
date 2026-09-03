namespace AIClient.Application.Configuration;

/// <summary>
/// Root of the application's settings tree. Every tunable value lives under one of
/// these sections rather than being scattered as constants across the code base.
/// Sections are persisted independently (one row per section) so adding a section
/// never requires a database migration.
/// </summary>
public sealed class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public ChatSettings Chat { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

    /// <summary>Section keys used as the primary key of the settings table.</summary>
    public static class Keys
    {
        public const string General = "general";
        public const string Appearance = "appearance";
        public const string Chat = "chat";
        public const string Storage = "storage";
    }
}
