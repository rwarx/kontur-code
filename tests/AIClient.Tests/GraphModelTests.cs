using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The vocabulary of the graph: how a kind is written down, and what a node and an edge promise.
/// </summary>
/// <remarks>
/// Small, cheap tests over types with no dependencies, and worth having anyway: the kinds are text
/// because the set is open, and text that is not canonicalised in exactly one way shows up as a
/// duplicate node in a user's project rather than as a failure here.
/// </remarks>
public sealed class GraphModelTests
{
    [Fact]
    public void The_unknown_kind_is_the_default_value()
    {
        // Kinds arrive from SQLite and from JSON, where an absent value deserialises to default. If
        // that were not equal to Unknown, every reader would need the null check the struct exists
        // to remove.
        Assert.Equal(GraphNodeKind.Unknown, default);
        Assert.Equal(GraphEdgeKind.Unknown, default);
        Assert.True(default(GraphNodeKind).IsUnknown);
        Assert.Equal("unknown", default(GraphNodeKind).Value);
        Assert.Equal("unknown", default(GraphEdgeKind).Value);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("FILE")]
    [InlineData("  File  ")]
    public void A_kind_is_recognised_however_it_was_written(string text) =>
        Assert.Equal(GraphNodeKind.File, GraphNodeKind.From(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("UNKNOWN")]
    public void Nothing_meaningful_parses_to_the_unknown_kind(string? text)
    {
        Assert.True(GraphNodeKind.From(text).IsUnknown);
        Assert.True(GraphEdgeKind.From(text).IsUnknown);
    }

    [Fact]
    public void A_kind_nobody_declared_survives_the_round_trip()
    {
        // The reason these are not enums. Section 3 names over twenty kinds and then says "and other
        // entities", so a kind an indexer or a model invents has to reach the database and come back
        // intact rather than being flattened to unknown on the way.
        var kind = GraphNodeKind.From("Saga");

        Assert.False(kind.IsUnknown);
        Assert.Equal("saga", kind.Value);
        Assert.Equal(kind, GraphNodeKind.From(kind.Value));
    }

    [Fact]
    public void Well_known_kinds_keep_the_text_the_database_stores()
    {
        // Pinned because these strings are in rows on a user's disk. Renaming one silently orphans
        // every node already written under the old spelling.
        Assert.Equal("file", GraphNodeKind.File.Value);
        Assert.Equal("folder", GraphNodeKind.Folder.Value);
        Assert.Equal("execution", GraphNodeKind.Execution.Value);
        Assert.Equal("contains", GraphEdgeKind.Contains.Value);
        Assert.Equal("depends_on", GraphEdgeKind.DependsOn.Value);
        Assert.Equal("relates_to", GraphEdgeKind.RelatesTo.Value);
    }

    [Fact]
    public void A_node_kind_and_an_edge_kind_are_not_interchangeable()
    {
        // Both wrap a string, and both would accept the other's names. Separate types are what stop
        // "contains" being asked for as a node kind and quietly matching nothing.
        Assert.Equal("contains", GraphNodeKind.From("contains").Value);
        Assert.Equal("file", GraphEdgeKind.From("file").Value);
    }

    [Fact]
    public void A_node_records_no_metadata_until_something_writes_some()
    {
        var node = GraphSample.Node("src/Auth/AuthService.cs");

        Assert.Empty(node.Metadata);
        Assert.Same(GraphNode.NoMetadata, node.Metadata);
    }

    [Fact]
    public void Metadata_is_read_without_regard_to_case()
    {
        // Written by an indexer, read by a card that wants "language" and by a model that was told
        // about "Language". Both have to find it.
        var node = GraphSample.Node("src/Program.cs") with
        {
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Language"] = "csharp",
            },
        };

        Assert.Equal("csharp", node.Metadata["language"]);
    }

    [Fact]
    public void Ownership_follows_the_origin_and_nothing_else()
    {
        // The one bit the indexing invariant turns on, so it is worth stating plainly.
        Assert.True(GraphSample.Node("a", origin: GraphOrigin.Indexer).IsIndexerOwned);
        Assert.False(GraphSample.Node("b", origin: GraphOrigin.User).IsIndexerOwned);
        Assert.False(GraphSample.Node("c", origin: GraphOrigin.Agent).IsIndexerOwned);
    }

    [Fact]
    public void An_edge_gives_the_other_end_and_nothing_for_a_node_it_does_not_touch()
    {
        var from = GraphSample.Node("src/Auth");
        var to = GraphSample.Node("src/Auth/AuthService.cs");
        var edge = GraphSample.Edge(from, to);

        Assert.Equal(to.Id, edge.Other(from.Id));
        Assert.Equal(from.Id, edge.Other(to.Id));
        Assert.Null(edge.Other(Guid.CreateVersion7()));
    }
}
