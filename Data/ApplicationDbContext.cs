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

    public DbSet<LineKindProgress> LineKindProgress => Set<LineKindProgress>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

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
