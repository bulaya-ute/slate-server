using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Slate.Server.Domain;

namespace Slate.Server.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Username).IsRequired();
        builder.HasIndex(u => u.Username).IsUnique();

        builder.Property(u => u.DisplayName).IsRequired();
        builder.Property(u => u.PasswordHash).IsRequired();

        builder.Property(u => u.Role)
            .HasConversion(EnumConversions.ForEnum<UserRole>())
            .IsRequired();

        builder.HasMany(u => u.RefreshTokens)
            .WithOne(rt => rt.User)
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.ExternalLogins)
            .WithOne(el => el.User)
            .HasForeignKey(el => el.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.OwnedVaults)
            .WithOne(v => v.Owner)
            .HasForeignKey(v => v.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(u => u.VaultMemberships)
            .WithOne(vm => vm.User)
            .HasForeignKey(vm => vm.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
