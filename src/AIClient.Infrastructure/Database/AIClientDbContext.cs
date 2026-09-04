using AIClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AIClient.Infrastructure.Database;

/// <summary>
/// EF Core context over the local SQLite file.
/// </summary>
/// <remarks>
/// Registered as a factory rather than a scoped instance. WPF has no per-request scope, a
/// DbContext is not thread-safe, and streaming a chat means writes from a background task
/// while the UI reads on the dispatcher thread. A short-lived context per operation is the
/// only shape that stays correct under those conditions.
/// </remarks>
public sealed class AIClientDbContext : DbContext
{
    public AIClientDbContext(DbContextOptions<AIClientDbContext> options)
        : base(options)
    {
    }

    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Model> Models => Set<Model>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AppSettingsEntry> Settings => Set<AppSettingsEntry>();

    public DbSet<GraphNodeRow> GraphNodes => Set<GraphNodeRow>();
    public DbSet<GraphEdgeRow> GraphEdges => Set<GraphEdgeRow>();
    public DbSet<GraphChangeRow> GraphChanges => Set<GraphChangeRow>();

    // The spatial half. Kept in three tables of their own rather than as columns on GraphNodes so
    // that the separation is enforced by the schema: there is nowhere here to record a fact about
    // the project, and nowhere there to record a position.
    public DbSet<CanvasViewRow> CanvasViews => Set<CanvasViewRow>();
    public DbSet<CanvasPlacementRow> CanvasPlacements => Set<CanvasPlacementRow>();
    public DbSet<CanvasAreaRow> CanvasAreas => Set<CanvasAreaRow>();

    /// <summary>
    /// Applies the timestamp conversion once, for the whole model.
    /// </summary>
    /// <remarks>
    /// Per-property configuration would work but would have to be remembered every time an
    /// entity gains a date, and forgetting it fails at query time rather than at build time.
    /// A pre-convention rule covers nullable properties as well, so
    /// <see cref="Provider.ModelsRefreshedAt"/> is included without being named.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Provider>(entity =>
        {
            entity.ToTable("Providers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.Name).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Type).HasMaxLength(64).IsRequired();
            entity.Property(e => e.BaseUrlOverride).HasMaxLength(512);
        });

        modelBuilder.Entity<Model>(entity =>
        {
            entity.ToTable("Models");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256);
            entity.Property(e => e.ProviderId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ModelId).HasMaxLength(192).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SupportedParameters).HasMaxLength(512);

            entity.HasOne(e => e.Provider)
                .WithMany(p => p.Models)
                .HasForeignKey(e => e.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);

            // The model picker filters by provider on every open.
            entity.HasIndex(e => e.ProviderId);

            // Lookups during a chat turn are always (provider, native id).
            entity.HasIndex(e => new { e.ProviderId, e.ModelId }).IsUnique();
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("Conversations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProviderId).HasMaxLength(64);
            entity.Property(e => e.ModelId).HasMaxLength(192);

            // The sidebar orders by pinned then recency; a composite index serves it directly.
            entity.HasIndex(e => new { e.IsPinned, e.UpdatedAt });
            entity.HasIndex(e => e.UpdatedAt);
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("Messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.ProviderId).HasMaxLength(64);
            entity.Property(e => e.ModelId).HasMaxLength(192);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2048);

            // Tool ids are provider-issued and short; the name matches an IAgentTool.Name. The
            // call array is left unbounded because one step may call several tools and an
            // argument object can legitimately carry a file's worth of text.
            entity.Property(e => e.ToolCallId).HasMaxLength(128);
            entity.Property(e => e.ToolName).HasMaxLength(64);

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Loading a conversation always reads its messages in order.
            entity.HasIndex(e => new { e.ConversationId, e.SequenceNumber });
        });

        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.ToTable("Attachments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).HasMaxLength(260).IsRequired();
            entity.Property(e => e.MimeType).HasMaxLength(128).IsRequired();
            entity.Property(e => e.StoredPath).HasMaxLength(512);

            entity.HasOne(e => e.Message)
                .WithMany(m => m.Attachments)
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.MessageId);
        });

        modelBuilder.Entity<AppSettingsEntry>(entity =>
        {
            entity.ToTable("Settings");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(64);
            entity.Property(e => e.Value).IsRequired();
        });

        ConfigureGraph(modelBuilder);
        ConfigureCanvas(modelBuilder);
    }

    /// <summary>
    /// The knowledge graph: what is true about the project, and the log of how it got that way.
    /// </summary>
    private static void ConfigureGraph(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GraphNodeRow>(entity =>
        {
            entity.ToTable("GraphNodes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Key).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Summary).HasMaxLength(2048);

            // Long enough for any path WorkspacePath will accept, which caps at 400 characters.
            entity.Property(e => e.SourcePath).HasMaxLength(512);

            // The identity of a node, and the reason re-indexing is an upsert rather than a
            // rebuild: the walk looks a node up by (kind, key), so the id survives, and with the
            // id every placement and every hand-drawn edge pointing at it survives too.
            entity.HasIndex(e => new { e.Kind, e.Key }).IsUnique();

            // A file that disappeared becomes Missing rather than being deleted, so both the
            // "what is stale" sweep and the ordinary "draw the live graph" read filter on this.
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<GraphEdgeRow>(entity =>
        {
            entity.ToTable("GraphEdges");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Label).HasMaxLength(256);

            // No navigation properties on purpose. An edge is not owned by either of its ends, and
            // giving it two would invite loading a graph one node at a time.
            entity.HasOne<GraphNodeRow>()
                .WithMany()
                .HasForeignKey(e => e.FromId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<GraphNodeRow>()
                .WithMany()
                .HasForeignKey(e => e.ToId)
                .OnDelete(DeleteBehavior.Cascade);

            // Traversal goes both ways - dependencies and dependents - so both ends are indexed.
            entity.HasIndex(e => e.FromId);
            entity.HasIndex(e => e.ToId);

            // The kind belongs in the key: "A depends_on B" and "A calls B" are two facts about
            // one pair, and both are allowed.
            entity.HasIndex(e => new { e.FromId, e.ToId, e.Kind }).IsUnique();
        });

        modelBuilder.Entity<GraphChangeRow>(entity =>
        {
            entity.ToTable("GraphChanges");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Summary).HasMaxLength(512).IsRequired();
            entity.Property(e => e.MutationsJson).IsRequired();

            // The timeline reads newest first, and undo reads the newest applied entry.
            entity.HasIndex(e => e.CreatedAt);

            // A model's suggestion sits here until someone accepts it; the canvas asks for exactly
            // the proposed ones on every load.
            entity.HasIndex(e => e.State);
        });
    }

    /// <summary>
    /// The spatial projection: where things are drawn, and nothing about what they are.
    /// </summary>
    /// <remarks>
    /// The test of this schema is that dropping all three of these tables loses no fact about the
    /// project - the next open recomputes a layout and everything else is still in the graph. That
    /// is why no column here carries meaning, and why every foreign key points from here into the
    /// graph rather than the other way round: a projection may depend on the truth, never the
    /// reverse.
    /// </remarks>
    private static void ConfigureCanvas(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CanvasViewRow>(entity =>
        {
            entity.ToTable("CanvasViews");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(128).IsRequired();
            entity.Property(e => e.LayoutMode).HasMaxLength(32).IsRequired();

            // Clearing rather than cascading: a view whose root node was deleted becomes a view of
            // the whole graph, which is recoverable. Deleting the view with it would throw away the
            // arrangement of every other card on it.
            entity.HasOne<GraphNodeRow>()
                .WithMany()
                .HasForeignKey(e => e.RootNodeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CanvasPlacementRow>(entity =>
        {
            entity.ToTable("CanvasPlacements");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Accent).HasMaxLength(32);

            entity.HasOne(e => e.View)
                .WithMany()
                .HasForeignKey(e => e.ViewId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<GraphNodeRow>()
                .WithMany()
                .HasForeignKey(e => e.NodeId)
                .OnDelete(DeleteBehavior.Cascade);

            // One position per node per view. The same node sits somewhere else on another view,
            // which is what having views is for.
            entity.HasIndex(e => new { e.ViewId, e.NodeId }).IsUnique();

            // "Which views show this node" - asked when a node is opened from the inspector or
            // from a search result.
            entity.HasIndex(e => e.NodeId);
        });

        modelBuilder.Entity<CanvasAreaRow>(entity =>
        {
            entity.ToTable("CanvasAreas");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Accent).HasMaxLength(32);

            entity.HasOne(e => e.View)
                .WithMany()
                .HasForeignKey(e => e.ViewId)
                .OnDelete(DeleteBehavior.Cascade);

            // A frame outlives the component it stood for, and becomes a plain visual divider.
            entity.HasOne<GraphNodeRow>()
                .WithMany()
                .HasForeignKey(e => e.GroupNodeId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.ViewId);
        });
    }
}
