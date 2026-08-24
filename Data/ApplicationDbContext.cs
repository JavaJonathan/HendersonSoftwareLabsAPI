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
    }
}
