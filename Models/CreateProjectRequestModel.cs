namespace HendersonSoftwareLabsAPI.Models;

public class CreateProjectRequestModel
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Url { get; set; }
}
