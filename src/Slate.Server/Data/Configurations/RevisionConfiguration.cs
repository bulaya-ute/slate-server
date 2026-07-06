using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Slate.Server.Domain;

namespace Slate.Server.Data.Configurations;

public class RevisionConfiguration : IEntityTypeConfiguration<Revision>
{
    public void Configure(EntityTypeBuilder<Revision> builder)
    {
        builder.ToTable("revisions");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.DeviceId).IsRequired();
        builder.Property(r => r.Path).IsRequired();
        builder.Property(r => r.ContentHash).IsRequired();

        builder.Property(r => r.Kind)
            .HasConversion(EnumConversions.ForEnum<RevisionKind>())
            .IsRequired();

        // Clients page catch-up with WHERE vault_id = @v AND id > @since.
        builder.HasIndex(r => new { r.VaultId, r.Id });

        builder.HasOne(r => r.Author)
            .WithMany()
            .HasForeignKey(r => r.AuthorId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(r => r.ParentRevision)
            .WithMany()
            .HasForeignKey(r => r.ParentRevId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
