using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Slate.Server.Domain;

namespace Slate.Server.Data.Configurations;

public class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Path).IsRequired();
        builder.Property(a => a.ContentHash).IsRequired();
        builder.Property(a => a.Mime).IsRequired();

        builder.HasIndex(a => new { a.VaultId, a.Path }).IsUnique();
    }
}
