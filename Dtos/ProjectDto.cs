using System.Linq.Expressions;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Dtos;

public class ProjectDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Url { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>EF-translatable projection, shared by every query that maps a SoftwareProject to a ProjectDto.</summary>
    public static readonly Expression<Func<SoftwareProject, ProjectDto>> FromEntity = p => new ProjectDto
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        Status = p.Status.ToString(),
        Url = p.Url,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt
    };
}
