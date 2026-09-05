namespace AIClient.App.Controls;

/// <summary>
/// The vocabulary of the icon set. One member, one 16×16 stroke drawing declared in
/// <c>Resources/Design/Icons.xaml</c> under the resource key <c>Icon.&lt;Kind&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Icons carry meaning, not decoration: every member here exists because a surface in the
/// product refers to that exact concept. Adding an icon means adding a member and its
/// drawing - never a Unicode glyph, never an emoji, both of which render at the mercy of the
/// host font and break the optical consistency this set is built for.
/// </para>
/// <para>
/// Drawings share one grid (16×16, ~1.5px strokes, round caps and joins) so that a row of
/// mixed icons keeps an even optical weight - a thick filled square beside a thin stroke
/// outline reads as a mistake even when nothing is technically wrong.
/// </para>
/// </remarks>
public enum IconKind
{
    /// <summary>The Kontur mark; the product's own glyph, used in the shell identity row.</summary>
    Logo,

    // ---- Navigation ---------------------------------------------------------

    Chat,
    Canvas,
    Graph,
    Files,
    Code,
    Models,
    Tasks,
    Memory,
    Settings,
    Search,
    Command,

    // ---- Workspace ----------------------------------------------------------

    Folder,
    File,
    Node,
    Link,
    Package,
    Filter,

    // ---- Actions ------------------------------------------------------------

    Plus,
    Close,
    ChevronUp,
    ChevronDown,
    ChevronLeft,
    ChevronRight,
    CollapsePanel,
    ExpandPanel,
    Refresh,
    Undo,
    Redo,
    ZoomIn,
    ZoomOut,
    Fit,
    Focus,
    Pin,
    Trash,
    Edit,
    Copy,
    Export,
    Save,
    Open,
    Play,
    Stop,
    Send,
    More,

    // ---- States -------------------------------------------------------------

    Sparkle,
    Warning,
    Error,
    Check,
    Info,
    Eye,
    Clock,
    Bot,
    User,
    Note,
}
