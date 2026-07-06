using Microsoft.EntityFrameworkCore;
using Slate.Server.Domain;

namespace Slate.Server.Data;

public class SlateDbContext : DbContext
{
    public SlateDbContext(DbContextOptions<SlateDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<Vault> Vaults => Set<Vault>();
    public DbSet<VaultMember> VaultMembers => Set<VaultMember>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Revision> Revisions => Set<Revision>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<NoteTag> NoteTags => Set<NoteTag>();
    public DbSet<Link> Links => Set<Link>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SlateDbContext).Assembly);
    }
}
