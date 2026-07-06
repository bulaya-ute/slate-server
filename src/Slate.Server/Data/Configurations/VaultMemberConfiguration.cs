using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Slate.Server.Domain;

namespace Slate.Server.Data.Configurations;

public class VaultMemberConfiguration : IEntityTypeConfiguration<VaultMember>
{
    public void Configure(EntityTypeBuilder<VaultMember> builder)
    {
        builder.ToTable("vault_members");
        builder.HasKey(vm => new { vm.VaultId, vm.UserId });

        builder.Property(vm => vm.Access)
            .HasConversion(EnumConversions.ForEnum<VaultAccessLevel>())
            .IsRequired();
    }
}
