namespace HendersonSoftwareLabsAPI.Dtos;

public class AdminClientListItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public int ProjectCount { get; set; }
}
