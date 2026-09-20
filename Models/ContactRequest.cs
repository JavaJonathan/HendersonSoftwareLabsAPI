using System.ComponentModel.DataAnnotations;

namespace HendersonSoftwareLabsAPI.Models;

public class ContactRequest : IValidatableObject
{
    public Guid SubmissionId { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(5000)] public string Message { get; set; } = "";
    [StringLength(200)] public string? Website { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (SubmissionId == Guid.Empty)
            yield return new ValidationResult("A submission ID is required.", [nameof(SubmissionId)]);
    }
}
