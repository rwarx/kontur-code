namespace AIClient.Application.Configuration;

/// <summary>
/// Resolves the per-user paths the application writes to. Centralised so that no other
/// class ever calls <see cref="Environment.GetFolderPath"/> and so tests can redirect
/// everything by substituting this service.
/// </summary>
public interface IAppPaths
{
    /// <summary>Root under %APPDATA%. Created on first access.</summary>
    string DataDirectory { get; }

    /// <summary>Full path of the SQLite file.</summary>
    string DatabasePath { get; }

    /// <summary>Directory holding rolling log files.</summary>
    string LogsDirectory { get; }

    /// <summary>Directory holding copied attachments.</summary>
    string AttachmentsDirectory { get; }

    /// <summary>Directory holding DPAPI-encrypted secrets.</summary>
    string SecretsDirectory { get; }
}
