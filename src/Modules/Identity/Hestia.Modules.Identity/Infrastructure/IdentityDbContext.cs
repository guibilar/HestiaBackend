using Hestia.Modules.Identity.Domain;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Identity.Infrastructure;

/// <summary>
/// The sign-in tables, in the schema <c>identity</c>: the ASP.NET Core Identity users (the same tables as in the
/// finance backend), the sessions, and the keys that encrypt the session cookie.
/// </summary>
/// <remarks>
/// A module DbContext derives from <see cref="ModuleDbContext"/>, so it cannot derive from the Identity base class
/// (<c>IdentityUserContext</c>). <see cref="OnModelCreating"/> maps the same entities, with the same table names,
/// columns, and indexes. The user store is <c>UserOnlyStore</c> on this context (no roles).
/// </remarks>
public class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : ModuleDbContext(options, SchemaName), IDataProtectionKeyContext
{
    public const string SchemaName = "identity";

    /// <summary>The maximum length of the user name and the e-mail address (the Identity default).</summary>
    private const int NameLength = 256;

    public DbSet<IdentityUser> Users => Set<IdentityUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<IdentityUser>(user =>
        {
            user.ToTable("AspNetUsers");
            user.HasKey(u => u.Id);
            user.HasIndex(u => u.NormalizedUserName).HasDatabaseName("UserNameIndex").IsUnique();
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("EmailIndex");
            user.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            user.Property(u => u.UserName).HasMaxLength(NameLength);
            user.Property(u => u.NormalizedUserName).HasMaxLength(NameLength);
            user.Property(u => u.Email).HasMaxLength(NameLength);
            user.Property(u => u.NormalizedEmail).HasMaxLength(NameLength);
            user.HasMany<IdentityUserClaim<string>>().WithOne().HasForeignKey(c => c.UserId).IsRequired();
            user.HasMany<IdentityUserLogin<string>>().WithOne().HasForeignKey(l => l.UserId).IsRequired();
            user.HasMany<IdentityUserToken<string>>().WithOne().HasForeignKey(t => t.UserId).IsRequired();
        });

        modelBuilder.Entity<IdentityUserClaim<string>>(claim =>
        {
            claim.ToTable("AspNetUserClaims");
            claim.HasKey(c => c.Id);
        });

        modelBuilder.Entity<IdentityUserLogin<string>>(login =>
        {
            login.ToTable("AspNetUserLogins");
            login.HasKey(l => new { l.LoginProvider, l.ProviderKey });
        });

        modelBuilder.Entity<IdentityUserToken<string>>(token =>
        {
            token.ToTable("AspNetUserTokens");
            token.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });
        });

        modelBuilder.Entity<UserSession>(session =>
        {
            session.ToTable("UserSessions");
            session.HasKey(s => s.Id);
            session.Property(s => s.Id).HasMaxLength(UserSession.IdLength);
            // Deleting the account ends its sessions.
            session.HasOne<IdentityUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DataProtectionKey>(key => key.ToTable("DataProtectionKeys"));
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class IdentityDbContextDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<IdentityDbContext>(IdentityDbContext.SchemaName));
}
