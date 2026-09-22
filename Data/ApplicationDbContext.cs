using HendersonSoftwareLabsAPI.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<SoftwareProject> SoftwareProjects => Set<SoftwareProject>();
    public DbSet<Inquiry> Inquiries => Set<Inquiry>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<ActiveProjectDetail> ActiveProjectDetails => Set<ActiveProjectDetail>();
    public DbSet<BusinessProspectDetail> BusinessProspectDetails => Set<BusinessProspectDetail>();
    public DbSet<OpportunityEvaluation> OpportunityEvaluations => Set<OpportunityEvaluation>();
    public DbSet<RadarPreferences> RadarPreferences => Set<RadarPreferences>();

    public DbSet<LineKindProgress> LineKindProgress => Set<LineKindProgress>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Inquiry>(entity =>
        {
            entity.HasIndex(p => p.SubmissionId).IsUnique();
            entity.HasIndex(p => new { p.Status, p.CreatedAt, p.Id });
            entity.Property(p => p.Name).HasMaxLength(100);
            entity.Property(p => p.Email).HasMaxLength(254);
            entity.Property(p => p.Message).HasMaxLength(5000);
            entity.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        });

        builder.Entity<SoftwareProject>(entity =>
        {
            entity.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(p => p.Name).HasMaxLength(200);
            entity.Property(p => p.Description).HasMaxLength(2000);
            entity.Property(p => p.Url).HasMaxLength(500);

            entity.HasOne(p => p.ClientUser)
                .WithMany(u => u.SoftwareProjects)
                .HasForeignKey(p => p.ClientUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Opportunity>(entity =>
        {
            entity.Property(x => x.EntityType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(30000);
            entity.Property(x => x.SourceName).HasMaxLength(200);
            entity.Property(x => x.SourceUrl).HasMaxLength(1000);
            entity.Property(x => x.ExternalId).HasMaxLength(200);
            entity.Property(x => x.Fingerprint).HasMaxLength(64);
            entity.Property(x => x.SyntheticKey).HasMaxLength(100);
            entity.Property(x => x.Notes).HasMaxLength(5000);
            entity.Property(x => x.SourcePassagesJson).HasColumnType("jsonb");
            entity.HasIndex(x => x.EntityType);
            entity.HasIndex(x => x.Fingerprint);
            entity.HasIndex(x => x.SyntheticKey).IsUnique();
            entity.HasIndex(x => new { x.EntityType, x.ExternalId }).HasFilter("\"ExternalId\" IS NOT NULL");
            entity.HasOne(x => x.DuplicateOf).WithMany().HasForeignKey(x => x.DuplicateOfId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ActiveProjectDetail>(entity =>
        {
            entity.HasKey(x => x.OpportunityId);
            entity.Property(x => x.DeclaredSourceType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.UserDecision).HasConversion<string>().HasMaxLength(20);
            entity.HasOne(x => x.Opportunity).WithOne(x => x.ActiveProjectDetail)
                .HasForeignKey<ActiveProjectDetail>(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BusinessProspectDetail>(entity =>
        {
            entity.HasKey(x => x.OpportunityId);
            entity.Property(x => x.NormalizedBusinessName).HasMaxLength(200);
            entity.Property(x => x.WebsiteUrl).HasMaxLength(1000);
            entity.Property(x => x.NormalizedWebsiteDomain).HasMaxLength(255);
            entity.Property(x => x.Geography).HasMaxLength(200);
            entity.Property(x => x.Industry).HasMaxLength(200);
            entity.Property(x => x.UserDecision).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => x.NormalizedBusinessName);
            entity.HasIndex(x => x.NormalizedWebsiteDomain);
            entity.HasOne(x => x.Opportunity).WithOne(x => x.BusinessProspectDetail)
                .HasForeignKey<BusinessProspectDetail>(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OpportunityEvaluation>(entity =>
        {
            entity.Property(x => x.Provider).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Recommendation).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.PriorityBand).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.BudgetStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Model).HasMaxLength(100);
            entity.Property(x => x.QuestionSetVersion).HasMaxLength(50);
            entity.Property(x => x.AssessmentJson).HasColumnType("jsonb");
            entity.Property(x => x.ResultJson).HasColumnType("jsonb");
            entity.Property(x => x.ProviderResponseJson).HasColumnType("jsonb");
            entity.Property(x => x.Summary).HasMaxLength(1000);
            entity.Property(x => x.NextStep).HasMaxLength(1000);
            entity.Property(x => x.ErrorMessage).HasMaxLength(1000);
            entity.HasIndex(x => new { x.OpportunityId, x.CreatedAt });
            entity.HasOne(x => x.Opportunity).WithMany(x => x.Evaluations).HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RadarPreferences>(entity =>
        {
            entity.Property(x => x.OwnerUserId).HasMaxLength(450);
            entity.Property(x => x.ActiveProjectPreferencesJson).HasColumnType("jsonb");
            entity.Property(x => x.BusinessProspectPreferencesJson).HasColumnType("jsonb");
            entity.HasIndex(x => x.OwnerUserId).IsUnique();
            entity.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LineKindProgress>(entity =>
        {
            entity.HasKey(p => p.Kind);
            entity.Property(p => p.Kind).HasConversion<string>().HasMaxLength(32);

            // Exactly one row per kind, seeded here and never inserted or deleted at runtime. The
            // whole table is read at once, so there is nothing to index beyond the key.
            entity.HasData(SeedProgress());
        });
    }

    /// <summary>
    /// The Line's six kinds, as they exist on day one.
    ///
    /// Intake ships already automated, and that is a product decision rather than a convenience:
    /// without it the homepage would show only the manual half of the story for weeks, and the
    /// whole point is the contrast between a lane that clears itself and a lane you are clicking.
    /// Its timestamp has to be a compile-time constant for EF's deterministic seeding, and it is
    /// also the date the headline "tasks handled" figure has been counting up from ever since.
    /// </summary>
    private static LineKindProgress[] SeedProgress()
    {
        var launch = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

        return
        [
            new LineKindProgress { Kind = LineKind.Intake, UnlockedAt = launch },
            new LineKindProgress { Kind = LineKind.Validate },
            new LineKindProgress { Kind = LineKind.Invoice },
            new LineKindProgress { Kind = LineKind.Notify },
            new LineKindProgress { Kind = LineKind.Sync },
            new LineKindProgress { Kind = LineKind.Report }
        ];
    }
}
