using System.ComponentModel.DataAnnotations;

namespace HendersonSoftwareLabsAPI.Models;

public class LoginRequestModel
{
    [Required]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}
