namespace HendersonSoftwareLabsAPI.Entities;

public enum InquiryStatus { New, Contacted, Archived }

public class Inquiry
{
    public int Id { get; set; }
    public Guid SubmissionId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public InquiryStatus Status { get; set; } = InquiryStatus.New;
    public DateTime StatusUpdatedAt { get; set; }
}
