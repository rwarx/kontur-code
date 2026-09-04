using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Finds where something is written, across the project.
/// </summary>
/// <remarks>
/// The tool that makes a large project workable at all: it is the difference between a model reasoning
/// about the file it happens to have read and one that can find every caller of a method before
/// changing it.
/// </remarks>
public sealed class SearchFilesTool : WorkspaceTool
{
    private const int MaxSummaryQueryLength = 40;

    public SearchFilesTool(IWorkspaceService workspace)
        : base(workspace)
    {
    }

    public override string Name => "search_files";

    public override string Description =>
        "Searches the text files of the open project and returns the matching lines with their file and "
        + "line number. Use it to find where something is defined or used before changing it, and to look "
        + "inside a large file instead of reading the whole thing. The query is plain text unless you set "
        + "is_regex. Build output, dependency folders and files holding credentials are not searched.";

    public override string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Text to look for. Treated literally unless is_regex is true."
            },
            "path": {
              "type": "string",
              "description": "Folder to search under, relative to the project root. Omit it to search everything."
            },
            "file_pattern": {
              "type": "string",
              "description": "Glob on the file name alone, such as '*.cs'. Omit it to search every text file."
            },
            "is_regex": {
              "type": "boolean",
              "description": "Treat the query as a .NET regular expression. Off by default."
            },
            "match_case": {
              "type": "boolean",
              "description": "Match upper and lower case exactly. Off by default."
            }
          },
          "required": ["query"]
        }
        """;

    public override AgentToolRisk Risk => AgentToolRisk.Read;

    public override async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.TryGetString("query", out var query, out var queryError))
        {
            return Refuse(queryError);
        }

        if (!TryOptionalPath(arguments, "path", out var path, out var failure))
        {
            return failure;
        }

        var result = await Workspace
            .SearchAsync(
                new WorkspaceSearchQuery
                {
                    Query = query,
                    Path = path,
                    FilePattern = arguments.GetString("file_pattern"),
                    IsRegex = arguments.GetBoolean("is_regex"),
                    MatchCase = arguments.GetBoolean("match_case"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return Refuse(result.Error!);
        }

        var search = result.Value!;
        var summary = $"{Name} '{Clip(query)}'";

        if (search.Matches.Count == 0)
        {
            return Done(
                $"No matches for '{query}' in {search.FilesScanned} files. Try a shorter query, or a "
                + "different spelling of it - the search is literal unless is_regex is set.",
                summary);
        }

        var text = new StringBuilder()
            .Append(search.Matches.Count)
            .Append(search.Matches.Count == 1 ? " match in " : " matches in ")
            .Append(search.FilesScanned)
            .Append(search.FilesScanned == 1 ? " file:" : " files:");

        foreach (var match in search.Matches)
        {
            text.AppendLine()
                .Append(match.Path.Value)
                .Append(':')
                .Append(match.LineNumber)
                .Append(": ")
                .Append(match.Line);
        }

        if (search.IsTruncated)
        {
            text.AppendLine().Append(
                "The search stopped early, so there may be more. Narrow it with path or file_pattern.");
        }

        return Done(text.ToString(), $"{summary} ({search.Matches.Count})");
    }

    private static string Clip(string query) => query.Length <= MaxSummaryQueryLength
        ? query
        : string.Concat(query.AsSpan(0, MaxSummaryQueryLength), "…");
}
