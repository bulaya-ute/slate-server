using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Slate.Server.Domain;

namespace Slate.Server.Data.Configurations;

public class VaultConfiguration : IEntityTypeConfiguration<Vault>
{
    public void Configure(EntityTypeBuilder<Vault> builder)
    {
        builder.ToTable("vaults");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Name).IsRequired();

        builder.HasMany(v => v.Notes)
            .WithOne(n => n.Vault)
            .HasForeignKey(n => n.VaultId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(v => v.Revisions)
            .WithOne(r => r.Vault)
            .HasForeignKey(r => r.VaultId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(v => v.Attachments)
            .WithOne(a => a.Vault)
            .HasForeignKey(a => a.VaultId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(v => v.Tags)
            .WithOne(t => t.Vault)
            .HasForeignKey(t => t.VaultId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(v => v.Members)
            .WithOne(m => m.Vault)
            .HasForeignKey(m => m.VaultId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
