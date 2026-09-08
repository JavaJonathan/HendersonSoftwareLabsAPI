using System.ComponentModel.DataAnnotations;

namespace HendersonSoftwareLabsAPI.Models;

public class CreateClientRequestModel
{
    [Required]
    [EmailAddress]
    [StringLength(256, MinimumLength = 3)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string CompanyName { get; set; } = string.Empty;

    [StringLength(200)]
    public string? ContactName { get; set; }
}
