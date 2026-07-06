using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Slate.Server.Domain;

namespace Slate.Server.Data.Configurations;

public class LinkConfiguration : IEntityTypeConfiguration<Link>
{
    public void Configure(EntityTypeBuilder<Link> builder)
    {
        builder.ToTable("links");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.TargetText).IsRequired();

        builder.HasIndex(l => l.SourceNoteId);
        builder.HasIndex(l => l.TargetNoteId);
    }
}
