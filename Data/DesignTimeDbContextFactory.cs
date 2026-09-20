using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HendersonSoftwareLabsAPI.Data;

// Model generation must not start the web host or require JWT credentials.
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder().AddUserSecrets<ApplicationDbContext>().AddEnvironmentVariables().Build();
        var connection = config.GetConnectionString("Default")
            ?? "Host=localhost;Database=HendersonSoftwareLabs;Username=postgres";
        return new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options);
    }
}
