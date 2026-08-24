namespace HendersonSoftwareLabsAPI.Dtos;

public class CreateClientRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? ContactName { get; set; }
}
