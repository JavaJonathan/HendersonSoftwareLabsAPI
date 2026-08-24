using Microsoft.AspNetCore.Identity;

namespace HendersonSoftwareLabsAPI.Entities;

public class ApplicationUser : IdentityUser
{
    public string CompanyName { get; set; } = string.Empty;
    public string? ContactName { get; set; }

    public ICollection<SoftwareProject> SoftwareProjects { get; set; } = new List<SoftwareProject>();
}
