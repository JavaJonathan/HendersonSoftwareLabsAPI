namespace HendersonSoftwareLabsAPI.Entities;

public class SoftwareProject
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ProjectStatus Status { get; set; }
    public string? Url { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public string ClientUserId { get; set; } = string.Empty;
    public ApplicationUser ClientUser { get; set; } = null!;
}
