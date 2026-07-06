using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Slate.Server.Domain;

namespace Slate.Server.Data.Configurations;

public class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.ToTable("notes");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Path).IsRequired();
        builder.Property(n => n.Title).IsRequired();
        builder.Property(n => n.ContentHash).IsRequired();

        builder.Property(n => n.SearchVector).HasColumnType("tsvector");
        builder.HasIndex(n => n.SearchVector).HasMethod("gin");

        // Unique per vault among non-deleted notes only.
        builder.HasIndex(n => new { n.VaultId, n.Path })
            .IsUnique()
            .HasFilter("is_deleted = false");

        // Note <-> its head Revision. Nullable: the Note row is written before its first
        // Revision exists. Restrict: revisions are append-only and must not be deleted
        // out from under a note that points to them as head.
        builder.HasOne(n => n.HeadRevision)
            .WithMany()
            .HasForeignKey(n => n.HeadRevId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(n => n.Revisions)
            .WithOne(r => r.Note)
            .HasForeignKey(r => r.NoteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(n => n.NoteTags)
            .WithOne(nt => nt.Note)
            .HasForeignKey(nt => nt.NoteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(n => n.OutgoingLinks)
            .WithOne(l => l.SourceNote)
            .HasForeignKey(l => l.SourceNoteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(n => n.IncomingLinks)
            .WithOne(l => l.TargetNote)
            .HasForeignKey(l => l.TargetNoteId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
