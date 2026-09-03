namespace AIClient.Application.Configuration;

/// <summary>Which theme to apply. <see cref="System"/> follows the Windows setting live.</summary>
public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>Look and feel. Deliberately data-driven so new themes are a value, not a code change.</summary>
public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Fluent accent colour as <c>#RRGGBB</c>. Null uses the Windows accent colour.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Base font size for chat content, in device-independent pixels.</summary>
    public double ChatFontSize { get; set; } = 14;

    /// <summary>Monospace family for code blocks.</summary>
    public string CodeFontFamily { get; set; } = "Cascadia Code, Consolas, Courier New";

    public double CodeFontSize { get; set; } = 13;

    /// <summary>Mica backdrop. Turned off automatically on Windows versions that lack it.</summary>
    public bool UseMicaBackdrop { get; set; } = true;

    /// <summary>Cap on chat content width so lines stay readable on wide monitors. 0 disables the cap.</summary>
    public double MaxChatContentWidth { get; set; } = 860;
}
