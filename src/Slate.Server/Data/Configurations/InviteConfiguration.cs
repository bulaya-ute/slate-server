using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Slate.Server.Domain;

namespace Slate.Server.Data.Configurations;

public class InviteConfiguration : IEntityTypeConfiguration<Invite>
{
    public void Configure(EntityTypeBuilder<Invite> builder)
    {
        builder.ToTable("invites");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.TokenHash).IsRequired();
        builder.HasIndex(i => i.TokenHash).IsUnique();

        builder.Property(i => i.Role)
            .HasConversion(EnumConversions.ForEnum<UserRole>())
            .IsRequired();

        // Cascade (not Restrict): an invite is disposable admin tooling, not a durable record
        // that must outlive its creator. Deleting a user should not be blocked by invites they
        // issued - unused ones are simply gone, and any already-redeemed invite's used_by/used_at
        // audit trail lives independently (see used_by's SetNull below, keyed off the redeemer,
        // not the creator).
        builder.HasOne(i => i.CreatedByUser)
            .WithMany()
            .HasForeignKey(i => i.CreatedBy)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.UsedByUser)
            .WithMany()
            .HasForeignKey(i => i.UsedBy)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
