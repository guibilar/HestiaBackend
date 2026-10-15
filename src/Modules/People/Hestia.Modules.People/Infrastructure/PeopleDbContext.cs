using Hestia.Modules.People.Domain;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.People.Infrastructure;

public class PeopleDbContext(DbContextOptions<PeopleDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "people";

    public DbSet<Person> People => Set<Person>();
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<GiftIdea> Gifts => Set<GiftIdea>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<PersonLink> Links => Set<PersonLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Person>(person =>
        {
            person.ToTable("People");
            person.HasKey(p => p.Id);
            person.Property(p => p.Name).HasMaxLength(Person.MaxNameLength).IsRequired();
            person.Property(p => p.Nickname).HasMaxLength(Person.MaxNicknameLength);
            person.Property(p => p.Relationship).HasMaxLength(Person.MaxRelationshipLength);
            person.Property(p => p.Groups).HasMaxLength(PersonGroups.MaxStoredLength).IsRequired();
            person.Property(p => p.Email).HasMaxLength(Person.MaxEmailLength);
            person.Property(p => p.Phone).HasMaxLength(Person.MaxPhoneLength);
            person.Property(p => p.Notes).HasMaxLength(Person.MaxNotesLength);
            person.HasIndex(p => p.Name);

            // The contacts, the gift ideas, the loans, and the links go with the person.
            person.HasMany<Interaction>().WithOne().HasForeignKey(i => i.PersonId).OnDelete(DeleteBehavior.Cascade);
            person.HasMany<GiftIdea>().WithOne().HasForeignKey(g => g.PersonId).OnDelete(DeleteBehavior.Cascade);
            person.HasMany<Loan>().WithOne().HasForeignKey(l => l.PersonId).OnDelete(DeleteBehavior.Cascade);
            person.HasMany(p => p.Links).WithOne().HasForeignKey(l => l.PersonId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Interaction>(interaction =>
        {
            interaction.ToTable("Interactions");
            interaction.HasKey(i => i.Id);
            interaction.Property(i => i.Kind).HasMaxLength(Interaction.MaxKindLength);
            interaction.Property(i => i.Note).HasMaxLength(Interaction.MaxNoteLength);
            // The history of one person, newest first, and the latest contact.
            interaction.HasIndex(i => new { i.PersonId, i.OccurredOn });
        });

        modelBuilder.Entity<GiftIdea>(gift =>
        {
            gift.ToTable("GiftIdeas");
            gift.HasKey(g => g.Id);
            gift.Property(g => g.Title).HasMaxLength(GiftIdea.MaxTitleLength).IsRequired();
            gift.Property(g => g.Notes).HasMaxLength(GiftIdea.MaxNotesLength);
            gift.Property(g => g.Occasion).HasMaxLength(GiftIdea.MaxOccasionLength);
            gift.Property(g => g.Price).HasPrecision(PeopleText.AmountPrecision, PeopleText.AmountScale);
            gift.HasIndex(g => g.PersonId);
        });

        modelBuilder.Entity<Loan>(loan =>
        {
            loan.ToTable("Loans");
            loan.HasKey(l => l.Id);
            loan.Property(l => l.ItemName).HasMaxLength(Loan.MaxItemNameLength).IsRequired();
            loan.Property(l => l.Direction).HasConversion<string>().HasMaxLength(20);
            loan.Property(l => l.Notes).HasMaxLength(Loan.MaxNotesLength);
            loan.HasIndex(l => l.PersonId);
            // The open loans, and the overdue alerts.
            loan.HasIndex(l => l.ReturnedOn);
        });

        modelBuilder.Entity<PersonLink>(link =>
        {
            link.ToTable("PersonLinks");
            link.HasKey(l => l.Id);
            link.Property(l => l.Kind).HasMaxLength(PersonLink.MaxKindLength).IsRequired();
            link.Property(l => l.TargetId).HasMaxLength(PersonLink.MaxTargetIdLength).IsRequired();
            // One link per person and thing.
            link.HasIndex(l => new { l.PersonId, l.Kind, l.TargetId }).IsUnique();
            // GET /api/people/by-link, and the ...Deleted consumers: the people of one thing.
            link.HasIndex(l => new { l.Kind, l.TargetId });
        });

        // The inbox of the consumers of this module (T36): DocumentDeleted, TaskDeleted, and ItemDeleted.
        modelBuilder.AddEventInbox();
        // Durable events (T38): Timeline (T16) and later link kinds in other modules use them.
        modelBuilder.AddEventOutbox();
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class PeopleDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PeopleDbContext>
{
    public PeopleDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<PeopleDbContext>(PeopleDbContext.SchemaName));
}
