namespace HendersonSoftwareLabsAPI.Models;

public class MeResponseModel
{
    public string Email { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public bool IsAdmin { get; set; }
}
