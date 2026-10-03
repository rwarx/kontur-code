using System.Reflection;
using System.Text.Json;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Graph;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;
using AIClient.Domain.Workspace;

namespace AIClient.Server;

/// <summary>
/// Plain data endpoints: conversations, providers, workspace, graph, settings,
/// tools, export and checkpoints. No mocks: every read comes from the real
/// Application/Infrastructure services, every write goes through them.
/// </summary>
public static class DataEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        // The one route reachable without the bearer token, because a launcher probes for
        // readiness before it has anywhere to put a token, and because a readiness answer that
        // leaked anything would be a leak with no upside. It says the process is up and what
        // version it is; it says nothing about the user, the workspace, or the conversation store.
        var probe = app.MapGroup("/api");
        probe.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            version = typeof(DataEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            authentication = "bearer",
        }));

        // Everything else on this group is authorised. Two groups rather than an annotation per
        // route, so a route added later is protected by default and forgetting is not possible.
        var g = app.MapGroup("/api").RequireAuthorization();

        // ----- conversations -----
        g.MapGet("/conversations", async (int? skip, int? take, IConversationService s, CancellationToken ct) =>
            Results.Ok(await s.GetSummariesAsync(skip ?? 0, Math.Clamp(take ?? 50, 1, 200), ct).ConfigureAwait(false)));
        g.MapGet("/conversations/search", async (string q, int? take, IConversationService s, CancellationToken ct) =>
            Results.Ok(await s.SearchAsync(q, Math.Clamp(take ?? 50, 1, 200), ct).ConfigureAwait(false)));
        g.MapGet("/conversations/{id:guid}", async (Guid id, IConversationService s, CancellationToken ct) =>
            await s.GetAsync(id, ct).ConfigureAwait(false) is { } d ? Results.Ok(d) : Results.NotFound());

        g.MapPost("/conversations", async (NewConversationRequest req, IConversationService s, CancellationToken ct) =>
            Results.Ok(await s.CreateAsync(req.Title, req.ProviderId, req.ModelId, ct).ConfigureAwait(false)));

        g.MapPatch("/conversations/{id:guid}", async (Guid id, UpdateConversationRequest req, IConversationService s, CancellationToken ct) =>
        {
            if (req.Title is not null)
            {
                await s.RenameAsync(id, req.Title, ct).ConfigureAwait(false);
            }

            if (req.IsPinned is { } pinned)
            {
                await s.SetPinnedAsync(id, pinned, ct).ConfigureAwait(false);
            }

            if (req.ProviderId is not null && req.ModelId is not null)
            {
                await s.SetModelAsync(id, req.ProviderId, req.ModelId, ct).ConfigureAwait(false);
            }

            if (req.ProjectId is not null)
            {
                await s.SetProjectAsync(id, req.ProjectId == Guid.Empty ? null : req.ProjectId, ct).ConfigureAwait(false);
            }

            return Results.Ok(await s.GetAsync(id, ct).ConfigureAwait(false));
        });

        g.MapDelete("/conversations/{id:guid}", async (Guid id, IConversationService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new { deleted = true });
        });

        g.MapDelete("/messages/{id:guid}", async (Guid id, IConversationService s, CancellationToken ct) =>
        {
            await s.DeleteMessageAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new { deleted = true });
        });

        g.MapPatch("/messages/{id:guid}", async (Guid id, UpdateMessageRequest req, IConversationService s, CancellationToken ct) =>
        {
            await s.UpdateMessageAsync(new MessageUpdate { MessageId = id, Content = req.Content }, ct).ConfigureAwait(false);
            return Results.Ok(new { updated = true });
        });

        g.MapPost("/messages/{id:guid}/truncate-after", async (Guid id, IConversationService s, CancellationToken ct) =>
        {
            await s.DeleteFromMessageAsync(id, inclusive: false, ct).ConfigureAwait(false);
            return Results.Ok(new { truncated = true });
        });

        // ----- projects -----
        g.MapGet("/projects", async (IConversationService s, CancellationToken ct) =>
            Results.Ok(await s.GetProjectsAsync(ct).ConfigureAwait(false)));
        g.MapPost("/projects", async (NewProject req, IConversationService s, CancellationToken ct) =>
            Results.Ok(await s.CreateProjectAsync(req, ct).ConfigureAwait(false)));
        g.MapDelete("/projects/{id:guid}", async (Guid id, IConversationService s, CancellationToken ct) =>
        {
            await s.DeleteProjectAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new { deleted = true });
        });

        // ----- providers & models -----
        g.MapGet("/providers", async (IProviderRegistry r, CancellationToken ct) =>
            Results.Ok(await r.GetProvidersAsync(ct).ConfigureAwait(false)));
        g.MapGet("/providers/{id}/models", async (string id, IProviderRegistry r, CancellationToken ct) =>
            Results.Ok(await r.GetModelsAsync(id, ct).ConfigureAwait(false)));
        g.MapGet("/models", async (IProviderRegistry r, CancellationToken ct) =>
            Results.Ok(await r.GetAllModelsAsync(ct).ConfigureAwait(false)));
        g.MapPost("/providers/{id}/refresh", async (string id, IProviderRegistry r, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await r.RefreshModelsAsync(id, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        g.MapPost("/providers/{id}/key", async (string id, ApiKeyRequest req, IProviderRegistry r, CancellationToken ct) =>
        {
            await r.SetApiKeyAsync(id, req.ApiKey, ct).ConfigureAwait(false);
            return Results.Ok(new { saved = true });
        });
        g.MapDelete("/providers/{id}/key", async (string id, IProviderRegistry r, CancellationToken ct) =>
        {
            await r.DeleteApiKeyAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new { deleted = true });
        });
        g.MapPost("/providers/{id}/test", async (string id, IProviderRegistry r, CancellationToken ct) =>
            Results.Ok(await r.TestConnectionAsync(id, ct).ConfigureAwait(false)));
        g.MapPost("/providers/{id}/enabled", async (string id, EnabledRequest req, IProviderRegistry r, CancellationToken ct) =>
        {
            await r.SetEnabledAsync(id, req.IsEnabled, ct).ConfigureAwait(false);
            return Results.Ok(new { isEnabled = req.IsEnabled });
        });
        g.MapPost("/providers/custom", async (CustomProviderRequest req, IProviderRegistry r, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await r.AddCustomProviderAsync(req.Name, req.BaseUrl, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        g.MapDelete("/providers/{id}/custom", async (string id, IProviderRegistry r, CancellationToken ct) =>
        {
            try
            {
                await r.RemoveCustomProviderAsync(id, ct).ConfigureAwait(false);
                return Results.Ok(new { deleted = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ----- tools -----
        g.MapGet("/tools", (IAgentToolRegistry r) =>
            Results.Ok(r.Tools.Select(t => new { name = t.Name, description = t.Description, risk = t.Risk.ToString() })));

        // ----- workspace -----
        g.MapGet("/workspace", (IWorkspaceService w) =>
            Results.Ok(new { isOpen = w.IsOpen, root = w.Root }));
        g.MapPost("/workspace/open", async (OpenWorkspaceRequest req, IWorkspaceService w, CancellationToken ct) =>
        {
            var result = await w.OpenAsync(req.Directory, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(new { root = result.Value }) : Results.BadRequest(new { error = result.Error });
        });
        g.MapPost("/workspace/close", async (IWorkspaceService w, CancellationToken ct) =>
        {
            await w.CloseAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { closed = true });
        });
        g.MapGet("/workspace/list", async (string? path, bool? recursive, IWorkspaceService w, CancellationToken ct) =>
        {
            if (!WorkspacePath.TryParse(string.IsNullOrWhiteSpace(path) ? "." : path, out var p, out var perr))
            {
                return Results.BadRequest(new { error = perr });
            }

            var result = await w.ListAsync(p, recursive ?? false, ct).ConfigureAwait(false);
            return result.Success
                ? Results.Ok(new
                {
                    path = result.Value!.Path.Value,
                    isTruncated = result.Value.IsTruncated,
                    entries = result.Value.Entries.Select(e => new
                    {
                        path = e.Path.Value,
                        name = e.Path.Name,
                        isDirectory = e.IsDirectory,
                        size = e.Size,
                        modifiedAt = e.ModifiedAt,
                    }),
                })
                : Results.BadRequest(new { error = result.Error });
        });
        g.MapGet("/workspace/read", async (string path, int? startLine, int? lineCount, IWorkspaceService w, CancellationToken ct) =>
        {
            if (!WorkspacePath.TryParse(path, out var p, out var perr))
            {
                return Results.BadRequest(new { error = perr });
            }

            var result = await w.ReadAsync(p, startLine ?? 1, lineCount, ct).ConfigureAwait(false);
            return result.Success
                ? Results.Ok(new
                {
                    path = result.Value!.Path.Value,
                    content = result.Value.Content,
                    firstLine = result.Value.FirstLine,
                    lineCount = result.Value.LineCount,
                    totalLines = result.Value.TotalLines,
                    size = result.Value.Size,
                    isTruncated = result.Value.IsTruncated,
                })
                : Results.BadRequest(new { error = result.Error });
        });
        g.MapPost("/workspace/write", async (WriteFileRequest req, IWorkspaceService w, CancellationToken ct) =>
        {
            if (!WorkspacePath.TryParse(req.Path, out var p, out var perr))
            {
                return Results.BadRequest(new { error = perr });
            }

            var result = await w.WriteAsync(p, req.Content, ct).ConfigureAwait(false);
            return result.Success
                ? Results.Ok(new { created = result.Value!.Created, linesAfter = result.Value.LinesAfter })
                : Results.BadRequest(new { error = result.Error });
        });
        g.MapPost("/workspace/search", async (WorkspaceSearchQuery req, IWorkspaceService w, CancellationToken ct) =>
        {
            var result = await w.SearchAsync(req, ct).ConfigureAwait(false);
            return result.Success
                ? Results.Ok(new
                {
                    filesScanned = result.Value!.FilesScanned,
                    isTruncated = result.Value.IsTruncated,
                    matches = result.Value.Matches.Select(m => new
                    {
                        path = m.Path.Value,
                        lineNumber = m.LineNumber,
                        line = m.Line,
                    }),
                })
                : Results.BadRequest(new { error = result.Error });
        });
        g.MapPost("/workspace/mkdir", async (PathRequest req, IWorkspaceService w, CancellationToken ct) =>
        {
            if (!WorkspacePath.TryParse(req.Path, out var p, out var perr))
            {
                return Results.BadRequest(new { error = perr });
            }

            var result = await w.CreateDirectoryAsync(p, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(new { created = true }) : Results.BadRequest(new { error = result.Error });
        });
        g.MapDelete("/workspace/entry", async (string path, IWorkspaceService w, CancellationToken ct) =>
        {
            if (!WorkspacePath.TryParse(path, out var p, out var perr))
            {
                return Results.BadRequest(new { error = perr });
            }

            var result = await w.DeleteAsync(p, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(new { deleted = true }) : Results.BadRequest(new { error = result.Error });
        });
        g.MapPost("/workspace/move", async (MoveRequest req, IWorkspaceService w, CancellationToken ct) =>
        {
            if (!WorkspacePath.TryParse(req.From, out var from, out var ferr))
            {
                return Results.BadRequest(new { error = ferr });
            }

            if (!WorkspacePath.TryParse(req.To, out var to, out var terr))
            {
                return Results.BadRequest(new { error = terr });
            }

            var result = await w.MoveAsync(from, to, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(new { moved = true }) : Results.BadRequest(new { error = result.Error });
        });

        // ----- graph -----
        g.MapGet("/graph", (IGraphService graph) => Results.Ok(MapSnapshot(graph.Current)));
        g.MapGet("/graph/timeline", (IGraphService graph) =>
            Results.Ok(graph.Timeline.Select(t => new
            {
                id = t.Id,
                at = t.At,
                title = t.Title,
                description = t.Description,
                origin = t.Origin.ToString(),
                resultingVersion = t.ResultingVersion,
                nodeCount = t.NodeCount,
                edgeCount = t.EdgeCount,
                wasRejected = t.WasRejected,
            })));
        g.MapPost("/graph/undo", async (IGraphService graph, GraphPersistence persist, CancellationToken ct) =>
        {
            var result = await graph.UndoAsync(ct).ConfigureAwait(false);
            await persist.SaveCurrentAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { applied = result.Applied.Count, rejected = result.Rejected });
        });
        g.MapPost("/graph/redo", async (IGraphService graph, GraphPersistence persist, CancellationToken ct) =>
        {
            var result = await graph.RedoAsync(ct).ConfigureAwait(false);
            await persist.SaveCurrentAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { applied = result.Applied.Count, rejected = result.Rejected });
        });
        g.MapPost("/graph/reindex", async (WorkspaceGraphIndexer indexer, IGraphService graph, GraphPersistence persist, CancellationToken ct) =>
        {
            var result = await indexer.RebuildAsync(ct).ConfigureAwait(false);
            await persist.SaveCurrentAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { rejected = result.Rejected, snapshot = MapSnapshot(graph.Current) });
        });
        g.MapPost("/graph/apply", async (GraphApplyRequest req, IGraphService graph, GraphPersistence persist, CancellationToken ct) =>
        {
            var changes = new List<GraphChange>();
            foreach (var c in req.Changes)
            {
                if (BuildChange(c) is { } change)
                {
                    changes.Add(change);
                }
            }

            var set = new GraphChangeSet
            {
                Title = req.Title ?? "Canvas edit",
                Description = req.Description,
                Origin = GraphChangeOrigin.User,
                Changes = changes,
            };
            var result = await graph.ApplyAsync(set, ct).ConfigureAwait(false);
            await persist.SaveCurrentAsync(ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                applied = result.Applied.Count,
                rejected = result.Rejected,
                snapshot = MapSnapshot(result.Snapshot),
            });
        });

        // ----- settings -----
        g.MapGet("/settings", (ISettingsService s) => Results.Ok(s.Current));
        g.MapPut("/settings/{section}", async (string section, JsonElement body, ISettingsService s, CancellationToken ct) =>
        {
            try
            {
                await ApplySectionAsync(s, section, body, ct).ConfigureAwait(false);
                return Results.Ok(s.Current);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ----- export -----
        g.MapGet("/export/conversation/{id:guid}", async (
            Guid id, string? format, IConversationService conversations, IExportService export, CancellationToken ct) =>
        {
            var detail = await conversations.GetAsync(id, ct).ConfigureAwait(false);
            if (detail is null)
            {
                return Results.NotFound();
            }

            var fmt = format?.ToLowerInvariant() switch
            {
                "json" => ExportFormat.Json,
                "text" or "txt" => ExportFormat.PlainText,
                _ => ExportFormat.Markdown,
            };
            var content = export.Export(detail, fmt);
            return Results.Text(content, "text/plain; charset=utf-8");
        });

        // ----- checkpoints -----
        g.MapGet("/checkpoints", async (JsonCheckpointStore store, CancellationToken ct) =>
            Results.Ok((await store.ListAsync(ct).ConfigureAwait(false))
                .Select(c => new { c.Id, c.ConversationId, c.Label, c.CreatedAt, c.MessageId, fileCount = c.Files.Count, hasGraph = c.GraphKey != null })));
        g.MapGet("/checkpoints/{id:guid}", async (Guid id, JsonCheckpointStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct).ConfigureAwait(false) is { } c ? Results.Ok(c) : Results.NotFound());
        g.MapPost("/checkpoints", async (
            CheckpointCreateRequest req, JsonCheckpointStore store, IWorkspaceService workspace,
            IGraphService graph, GraphPersistence persist, CancellationToken ct) =>
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in req.Files ?? [])
            {
                if (!WorkspacePath.TryParse(path, out var p, out _))
                {
                    continue;
                }

                var read = await workspace.ReadAsync(p, 1, null, ct).ConfigureAwait(false);
                if (read.Success && read.Value is { } file && file.Content.Length <= 256_000)
                {
                    files[p.Value] = file.Content;
                }
            }

            string? graphKey = null;
            if (req.IncludeGraph)
            {
                graphKey = "checkpoint:" + Guid.NewGuid().ToString("N");
                await graph.SaveAsync(graphKey, ct).ConfigureAwait(false);
            }

            var record = new CheckpointRecord
            {
                Id = Guid.NewGuid(),
                ConversationId = req.ConversationId,
                Label = req.Label,
                CreatedAt = DateTimeOffset.UtcNow,
                MessageId = req.MessageId,
                Files = files,
                GraphKey = graphKey,
            };
            await store.SaveAsync(record, ct).ConfigureAwait(false);
            return Results.Ok(new { id = record.Id });
        });
        g.MapPost("/checkpoints/{id:guid}/restore", async (
            Guid id, JsonCheckpointStore store, IWorkspaceService workspace,
            IGraphService graph, GraphPersistence persist, CancellationToken ct) =>
        {
            var record = await store.GetAsync(id, ct).ConfigureAwait(false);
            if (record is null)
            {
                return Results.NotFound();
            }

            var restored = 0;
            foreach (var (path, content) in record.Files)
            {
                if (!WorkspacePath.TryParse(path, out var p, out _))
                {
                    continue;
                }

                var write = await workspace.WriteAsync(p, content, ct).ConfigureAwait(false);
                if (write.Success)
                {
                    restored++;
                }
            }

            var graphRestored = false;
            if (record.GraphKey is { } key)
            {
                var snapshot = await graph.LoadAsync(key, ct).ConfigureAwait(false);
                graphRestored = snapshot.Nodes.Count > 0 || snapshot.Edges.Count > 0;
                await persist.SaveCurrentAsync(ct).ConfigureAwait(false);
            }

            return Results.Ok(new { restoredFiles = restored, graphRestored });
        });
        g.MapDelete("/checkpoints/{id:guid}", async (Guid id, JsonCheckpointStore store, CancellationToken ct) =>
        {
            await store.DeleteAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new { deleted = true });
        });
    }

    // ----- request shapes -----
    private sealed record NewConversationRequest(string? Title, string? ProviderId, string? ModelId);
    private sealed record UpdateConversationRequest(string? Title, bool? IsPinned, string? ProviderId, string? ModelId, Guid? ProjectId);
    private sealed record UpdateMessageRequest(string Content);
    private sealed record ApiKeyRequest(string ApiKey);
    private sealed record EnabledRequest(bool IsEnabled);
    private sealed record CustomProviderRequest(string Name, string BaseUrl);
    private sealed record OpenWorkspaceRequest(string Directory);
    private sealed record WriteFileRequest(string Path, string Content);
    private sealed record PathRequest(string Path);
    private sealed record MoveRequest(string From, string To);

    private sealed record GraphNodeIn(
        string Id, string Kind, string Title, string? Subtitle, string? Detail,
        string? Path, double X, double Y, double? Width, double? Height);
    private sealed record GraphEdgeIn(string Id, string SourceId, string TargetId, string Kind, string? Label);
    private sealed record GraphChangeIn(
        string Kind, string? NodeId, GraphNodeIn? Node, string? Title, string? Subtitle,
        string? Detail, string? NodeKind, double? X, double? Y, string? EdgeId,
        GraphEdgeIn? Edge, string? EdgeKind, string? Label);
    private sealed record GraphApplyRequest(string? Title, string? Description, IReadOnlyList<GraphChangeIn> Changes);

    private sealed record CheckpointCreateRequest(
        Guid ConversationId, string Label, Guid? MessageId,
        IReadOnlyList<string>? Files, bool IncludeGraph);

    private static object MapSnapshot(GraphSnapshot s) => new
    {
        version = s.Version,
        nodes = s.Nodes.Select(n => new
        {
            id = n.Id,
            kind = n.Kind.ToString(),
            title = n.Title,
            subtitle = n.Subtitle,
            detail = n.Detail,
            path = n.Path,
            x = n.X,
            y = n.Y,
            width = n.Width,
            height = n.Height,
        }),
        edges = s.Edges.Select(e => new
        {
            id = e.Id,
            sourceId = e.SourceId,
            targetId = e.TargetId,
            kind = e.Kind.ToString(),
            label = e.Label,
        }),
    };

    private static GraphChange? BuildChange(GraphChangeIn c)
    {
        return c.Kind.ToLowerInvariant() switch
        {
            "add-node" when c.Node is { } n => new AddNode(new GraphNode
            {
                Id = n.Id,
                Kind = ParseKind<GraphNodeKind>(n.Kind, GraphNodeKind.Note),
                Title = n.Title,
                Subtitle = n.Subtitle,
                Detail = n.Detail,
                Path = n.Path,
                X = n.X,
                Y = n.Y,
                Width = n.Width is > 0 ? n.Width.Value : 200,
                Height = n.Height is > 0 ? n.Height.Value : 64,
            }),
            "update-node" when c.NodeId is { } id => new UpdateNode(
                id, c.Title, c.Subtitle, c.Detail,
                c.NodeKind is { } k ? ParseKind<GraphNodeKind?>(k, null) : null),
            "move-node" when c.NodeId is { } id && c.X is { } x && c.Y is { } y
                => new MoveNode(id, x, y),
            "remove-node" when c.NodeId is { } id => new RemoveNode(id),
            "add-edge" when c.Edge is { } e => new AddEdge(new GraphEdge
            {
                Id = e.Id,
                SourceId = e.SourceId,
                TargetId = e.TargetId,
                Kind = ParseKind<GraphEdgeKind>(e.Kind, GraphEdgeKind.Relates),
                Label = e.Label,
            }),
            "update-edge" when c.EdgeId is { } id => new UpdateEdge(
                id,
                c.EdgeKind is { } k ? ParseKind<GraphEdgeKind?>(k, null) : null,
                c.Label),
            "remove-edge" when c.EdgeId is { } id => new RemoveEdge(id),
            _ => null,
        };
    }

    private static T ParseKind<T>(string raw, T fallback)
    {
        try
        {
            if (Enum.TryParse(typeof(T), raw, ignoreCase: true, out var value) && value is T typed)
            {
                return typed;
            }
        }
        catch
        {
            // Fall through to the fallback.
        }

        return fallback;
    }

    private static readonly Dictionary<string, Func<AppSettings, object>> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = s => s.General,
        ["appearance"] = s => s.Appearance,
        ["chat"] = s => s.Chat,
        ["storage"] = s => s.Storage,
        ["agent"] = s => s.Agent,
        ["canvas"] = s => s.Canvas,
    };

    private static async Task ApplySectionAsync(
        ISettingsService settings, string section, JsonElement body, CancellationToken ct)
    {
        if (!Sections.TryGetValue(section, out var get))
        {
            throw new ArgumentException($"Unknown settings section '{section}'.");
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The settings body must be a JSON object.");
        }

        var current = get(settings.Current);
        var sectionType = current.GetType();
        var patched = JsonSerializer.Deserialize(body.GetRawText(), sectionType, Json)
            ?? throw new ArgumentException("The settings body could not be read.");

        // Apply ONLY the properties actually present in the request body; every other
        // field keeps its stored value. Without this filter a partial PUT (e.g.
        // { "maxSteps": 40 }) would deserialize into a fresh section object whose absent
        // properties carry that type's *default* initializers, and copying them all would
        // silently reset AllowCommands, the command allowlist, timeouts and every other
        // agent field back to defaults. Web JSON is camelCase; CLR props are PascalCase,
        // so match case-insensitively (no section uses [JsonPropertyName]).
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in body.EnumerateObject())
        {
            present.Add(member.Name);
        }

        var apply = typeof(DataEndpoints).GetMethod(
            nameof(ApplyPatched), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(sectionType);
        var task = (Task?)apply.Invoke(null, [settings, patched, present, ct]);
        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }
    }

    private static async Task ApplyPatched<TSection>(
        ISettingsService settings, TSection patched, HashSet<string> present, CancellationToken ct)
        where TSection : class
    {
        var props = typeof(TSection).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && present.Contains(p.Name))

            // Server-owned safety switches are not settable over HTTP, whatever the token. They turn
            // the agent's containment off - and the approval gate runs over the same authenticated
            // surface, so a caller who could set these could answer its own approvals. The user's own
            // Settings window is the only place these are changed, and that path goes through the WPF
            // host, not this endpoint. Refused rather than clamped: a silent clamp here would look to
            // the renderer like the setting had been accepted.
            .Where(p => !ServerOwnedSettings.Contains(p.Name))
            .ToList();

        if (props.Count == 0)
        {
            return;
        }

        var values = props.ToDictionary(p => p, p => p.GetValue(patched));
        await settings.UpdateAsync<TSection>(target =>
        {
            foreach (var (prop, value) in values)
            {
                prop.SetValue(target, value);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Settings a request may not write, by property name, across every section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named rather than typed because the properties live in <c>AIClient.Application.Configuration</c>
    /// and are matched against whatever section the request named. A name list is the fragile shape -
    /// a renamed or newly added property would not be caught - so this is the list to re-read when
    /// <c>AgentSettings</c> gains a field.
    /// </para>
    /// <para>
    /// Every entry is a switch whose purpose is to <em>reduce</em> containment. The bearer token makes
    /// this surface reachable only by the application; this list is what stops the application itself -
    /// a bug, a stale renderer, a replayed request - from turning the agent's guard rails off through
    /// a JSON body.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> ServerOwnedSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Program execution. The allowlist deliberately excludes cmd/powershell/bash; adding one to
        // the list over HTTP is the same as turning a sandbox off.
        nameof(AgentSettings.AllowCommands),
        nameof(AgentSettings.AllowedCommands),

        // Reaching outside the project folder and onto the network. Both are refused by a name-based
        // guard that is only as good as the list, and both are reachable through an approval gate this
        // same token can answer.
        nameof(AgentSettings.AllowExternalFiles),
        nameof(AgentSettings.AllowNetwork),

        // The folder itself. A request that can choose the root is a request that can hand the agent
        // a whole drive, which is what the path guard exists to prevent.
        nameof(AgentSettings.WorkspaceRoot),
    };
}
