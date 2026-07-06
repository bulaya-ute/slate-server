using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Slate.Server.Domain;

namespace Slate.Server.Data.Configurations;

public class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> builder)
    {
        builder.ToTable("external_logins");
        builder.HasKey(el => el.Id);

        builder.Property(el => el.Provider).IsRequired();
        builder.Property(el => el.Subject).IsRequired();

        builder.HasIndex(el => new { el.Provider, el.Subject }).IsUnique();
    }
}
