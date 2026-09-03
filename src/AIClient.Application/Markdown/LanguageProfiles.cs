using System.Collections.Frozen;

namespace AIClient.Application.Markdown;

/// <summary>
/// Lexical rules for one language family, consumed by <see cref="SyntaxHighlighter"/>.
/// </summary>
/// <remarks>
/// Grouped by family rather than per language: C#, Java, TypeScript and Go share the same
/// comment and string rules and differ only in keyword sets, so one profile with a merged
/// keyword list highlights all of them acceptably. Adding a language is a dictionary entry.
/// </remarks>
public sealed record LanguageProfile
{
    public required FrozenSet<string> Keywords { get; init; }
    public required FrozenSet<string> Types { get; init; }

    /// <summary>Characters that open a string literal.</summary>
    public required FrozenSet<char> StringDelimiters { get; init; }

    public string? LineComment { get; init; }
    public string? BlockCommentStart { get; init; }
    public string? BlockCommentEnd { get; init; }

    /// <summary>Prefix marking a whole-line directive, e.g. <c>#</c> for C preprocessor.</summary>
    public string? DirectivePrefix { get; init; }

    public bool SupportsBackslashEscapes { get; init; } = true;

    /// <summary>False makes an unterminated literal end at the newline, containing the damage.</summary>
    public bool AllowsMultilineStrings { get; init; }

    /// <summary>Colour PascalCase identifiers as types. Right for C#/Java/TS, wrong for Python.</summary>
    public bool TreatPascalCaseAsType { get; init; }
}

/// <summary>Language profile lookup, keyed by Markdown fence info string.</summary>
public static class LanguageProfiles
{
    private static readonly FrozenSet<char> DoubleAndSingleQuote = FrozenSet.ToFrozenSet(['"', '\'']);
    private static readonly FrozenSet<char> AllQuotes = FrozenSet.ToFrozenSet(['"', '\'', '`']);

    private static readonly LanguageProfile CSharpLike = new()
    {
        Keywords = Split("""
            abstract as async await base break case catch checked class const continue default delegate do
            else enum event explicit extern false finally fixed for foreach get goto if implicit in init
            interface internal is lock namespace new null operator out override params private protected
            public readonly record ref required return sealed set sizeof stackalloc static struct switch
            this throw true try typeof unchecked unsafe using var virtual void volatile when where while yield
            global partial nameof with and or not
            """),
        Types = Split("""
            bool byte char decimal double dynamic float int long object sbyte short string uint ulong ushort
            nint nuint Task ValueTask List Dictionary IEnumerable IReadOnlyList HashSet Span ReadOnlySpan
            Guid DateTime DateTimeOffset TimeSpan Exception CancellationToken IAsyncEnumerable
            """),
        StringDelimiters = DoubleAndSingleQuote,
        LineComment = "//",
        BlockCommentStart = "/*",
        BlockCommentEnd = "*/",
        DirectivePrefix = "#",
        TreatPascalCaseAsType = true,
    };

    private static readonly LanguageProfile JavaScriptLike = new()
    {
        Keywords = Split("""
            as async await break case catch class const continue debugger declare default delete do else enum
            export extends false finally for from function get if implements import in instanceof interface
            let new null of package private protected public readonly return satisfies set static super
            switch this throw true try type typeof var void while with yield keyof infer namespace abstract
            """),
        Types = Split("""
            any bigint boolean never number object string symbol undefined unknown Array Promise Map Set
            Record Partial Readonly Pick Omit Date RegExp Error JSON Math console window document
            """),
        StringDelimiters = AllQuotes,
        LineComment = "//",
        BlockCommentStart = "/*",
        BlockCommentEnd = "*/",
        TreatPascalCaseAsType = true,
    };

    private static readonly LanguageProfile Python = new()
    {
        Keywords = Split("""
            and as assert async await break class continue def del elif else except finally for from global
            if import in is lambda match case nonlocal not or pass raise return try while with yield None
            True False self cls
            """),
        Types = Split("""
            bool bytes complex dict float frozenset int list object set str tuple type Any Optional Union
            List Dict Tuple Callable Iterable Iterator Sequence Mapping print len range open enumerate zip
            """),
        StringDelimiters = DoubleAndSingleQuote,
        LineComment = "#",
        AllowsMultilineStrings = false,
    };

    private static readonly LanguageProfile CppLike = new()
    {
        Keywords = Split("""
            alignas alignof asm auto break case catch class concept const consteval constexpr constinit
            const_cast continue co_await co_return co_yield decltype default delete do dynamic_cast else
            enum explicit export extern false for friend goto if inline mutable namespace new noexcept
            nullptr operator private protected public register reinterpret_cast requires return sizeof
            static static_assert static_cast struct switch template this thread_local throw true try
            typedef typeid typename union using virtual volatile while
            """),
        Types = Split("""
            bool char char8_t char16_t char32_t double float int long short signed unsigned void wchar_t
            size_t ssize_t int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t
            string vector map set unordered_map shared_ptr unique_ptr optional variant span
            """),
        StringDelimiters = DoubleAndSingleQuote,
        LineComment = "//",
        BlockCommentStart = "/*",
        BlockCommentEnd = "*/",
        DirectivePrefix = "#",
    };

    private static readonly LanguageProfile Sql = new()
    {
        Keywords = Split("""
            ADD ALL ALTER AND ANY AS ASC BEGIN BETWEEN BY CASE CAST CHECK COLUMN COMMIT CONSTRAINT CREATE
            CROSS DEFAULT DELETE DESC DISTINCT DROP ELSE END EXCEPT EXEC EXISTS FOREIGN FROM FULL GROUP
            HAVING IF IN INDEX INNER INSERT INTERSECT INTO IS JOIN LEFT LIKE LIMIT NOT NULL OFFSET ON OR
            ORDER OUTER PRIMARY REFERENCES RIGHT ROLLBACK SELECT SET TABLE THEN TOP TRANSACTION TRUNCATE
            UNION UNIQUE UPDATE VALUES VIEW WHEN WHERE WITH
            add all alter and any as asc begin between by case cast check column commit constraint create
            cross default delete desc distinct drop else end except exec exists foreign from full group
            having if in index inner insert intersect into is join left like limit not null offset on or
            order outer primary references right rollback select set table then top transaction truncate
            union unique update values view when where with
            """),
        Types = Split("""
            BIGINT BIT BLOB BOOLEAN CHAR DATE DATETIME DECIMAL DOUBLE FLOAT INT INTEGER JSON NUMERIC REAL
            SMALLINT TEXT TIME TIMESTAMP UUID VARCHAR
            bigint bit blob boolean char date datetime decimal double float int integer json numeric real
            smallint text time timestamp uuid varchar
            """),
        StringDelimiters = DoubleAndSingleQuote,
        LineComment = "--",
        BlockCommentStart = "/*",
        BlockCommentEnd = "*/",
    };

    private static readonly LanguageProfile Shell = new()
    {
        Keywords = Split("""
            if then else elif fi for while until do done case esac function return break continue in
            export local readonly declare set unset source alias echo cd exit test
            """),
        Types = Split("git dotnet npm node python pip docker kubectl curl wget grep sed awk ls cat mkdir rm cp mv chmod sudo apt yum brew"),
        StringDelimiters = AllQuotes,
        LineComment = "#",
    };

    private static readonly LanguageProfile Json = new()
    {
        Keywords = Split("true false null"),
        Types = FrozenSet<string>.Empty,
        StringDelimiters = FrozenSet.ToFrozenSet(['"']),
    };

    private static readonly LanguageProfile Xml = new()
    {
        Keywords = FrozenSet<string>.Empty,
        Types = FrozenSet<string>.Empty,
        StringDelimiters = DoubleAndSingleQuote,
        BlockCommentStart = "<!--",
        BlockCommentEnd = "-->",
    };

    private static readonly LanguageProfile Yaml = new()
    {
        Keywords = Split("true false null yes no on off True False Null"),
        Types = FrozenSet<string>.Empty,
        StringDelimiters = DoubleAndSingleQuote,
        LineComment = "#",
    };

    /// <summary>
    /// Fence info string to profile. Aliases are listed explicitly rather than guessed,
    /// so an unknown language falls back predictably instead of being highlighted wrongly.
    /// </summary>
    private static readonly FrozenDictionary<string, LanguageProfile> Map =
        new Dictionary<string, LanguageProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = CSharpLike,
            ["cs"] = CSharpLike,
            ["c#"] = CSharpLike,
            ["dotnet"] = CSharpLike,
            ["java"] = CSharpLike,
            ["kotlin"] = CSharpLike,
            ["scala"] = CSharpLike,
            ["swift"] = CSharpLike,
            ["go"] = CSharpLike,
            ["golang"] = CSharpLike,
            ["rust"] = CSharpLike,
            ["rs"] = CSharpLike,

            ["javascript"] = JavaScriptLike,
            ["js"] = JavaScriptLike,
            ["jsx"] = JavaScriptLike,
            ["typescript"] = JavaScriptLike,
            ["ts"] = JavaScriptLike,
            ["tsx"] = JavaScriptLike,
            ["mjs"] = JavaScriptLike,
            ["cjs"] = JavaScriptLike,

            ["python"] = Python,
            ["py"] = Python,
            ["python3"] = Python,
            ["ruby"] = Python,
            ["rb"] = Python,

            ["c"] = CppLike,
            ["cpp"] = CppLike,
            ["c++"] = CppLike,
            ["cc"] = CppLike,
            ["h"] = CppLike,
            ["hpp"] = CppLike,
            ["objc"] = CppLike,

            ["sql"] = Sql,
            ["mysql"] = Sql,
            ["postgres"] = Sql,
            ["postgresql"] = Sql,
            ["sqlite"] = Sql,
            ["tsql"] = Sql,

            ["bash"] = Shell,
            ["sh"] = Shell,
            ["shell"] = Shell,
            ["zsh"] = Shell,
            ["console"] = Shell,
            ["powershell"] = Shell,
            ["ps1"] = Shell,

            ["json"] = Json,
            ["jsonc"] = Json,
            ["json5"] = Json,

            ["xml"] = Xml,
            ["html"] = Xml,
            ["xhtml"] = Xml,
            ["xaml"] = Xml,
            ["svg"] = Xml,
            ["csproj"] = Xml,

            ["yaml"] = Yaml,
            ["yml"] = Yaml,
            ["toml"] = Yaml,
            ["ini"] = Yaml,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a profile. An unrecognised language falls back to the C-like profile,
    /// which reads sensibly for most code; only an explicit <c>text</c>/<c>plain</c>
    /// fence disables highlighting entirely.
    /// </summary>
    public static LanguageProfile? Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return CSharpLike;
        }

        var key = language.Trim();

        // Fences sometimes carry extras: ```js title="x". Only the first word is the language.
        var space = key.IndexOfAny([' ', '\t', ',', ';', ':']);
        if (space > 0)
        {
            key = key[..space];
        }

        if (key is "text" or "plain" or "plaintext" or "txt" or "log" or "output" or "none" or "diff")
        {
            return null;
        }

        return Map.TryGetValue(key, out var profile) ? profile : CSharpLike;
    }

    /// <summary>True when the app has real rules for this language, as opposed to falling back.</summary>
    public static bool IsKnown(string? language) =>
        !string.IsNullOrWhiteSpace(language) && Map.ContainsKey(language.Trim());

    private static FrozenSet<string> Split(string words) =>
        words.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .ToFrozenSet(StringComparer.Ordinal);
}
