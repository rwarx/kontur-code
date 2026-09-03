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
    }
}
