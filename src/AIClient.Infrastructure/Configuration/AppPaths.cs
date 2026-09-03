using AIClient.Application.Configuration;

namespace AIClient.Infrastructure.Configuration;

/// <summary>
/// Resolves per-user paths under <c>%APPDATA%\AIClient</c> and creates the directories
/// on construction, so no caller ever has to check whether a folder exists.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    private const string ApplicationFolderName = "AIClient";

    public AppPaths()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create))
    {
    }

    /// <summary>Overload used by tests to redirect every path to a temporary root.</summary>
    public AppPaths(string rootDirectory)
    {
        DataDirectory = Path.Combine(rootDirectory, ApplicationFolderName);
        DatabasePath = Path.Combine(DataDirectory, "aiclient.db");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        AttachmentsDirectory = Path.Combine(DataDirectory, "attachments");
        SecretsDirectory = Path.Combine(DataDirectory, "secrets");

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
        Directory.CreateDirectory(SecretsDirectory);
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string LogsDirectory { get; }
    public string AttachmentsDirectory { get; }
    public string SecretsDirectory { get; }
}
