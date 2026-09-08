using System.ComponentModel.DataAnnotations;

namespace HendersonSoftwareLabsAPI.Models;

public class CreateProjectRequestModel
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [StringLength(32)]
    public string Status { get; set; } = string.Empty;

    // Optional; the UI omits it entirely when blank. Bounded to the column width.
    [StringLength(500)]
    public string? Url { get; set; }
}
