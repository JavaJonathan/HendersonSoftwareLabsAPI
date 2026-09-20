using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HendersonSoftwareLabsAPI.Controllers;

[ApiController, Route("api/contact"), AllowAnonymous]
public class ContactController(ApplicationDbContext db, ILogger<ContactController> logger) : ControllerBase
{
    [HttpPost, EnableRateLimiting("contact"), RequestSizeLimit(32768)]
    public async Task<IActionResult> Submit(ContactRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Website)) return BadRequest(new { message = "Unable to submit this inquiry. Please use the email alternative." });
        try
        {
            if (await db.Inquiries.AnyAsync(x => x.SubmissionId == request.SubmissionId, ct)) return Ok(new { received = true });
            var now = DateTime.UtcNow;
            var inquiry = new Inquiry { SubmissionId = request.SubmissionId, Name = request.Name.Trim(), Email = request.Email.Trim(), Message = request.Message.Trim(), CreatedAt = now, StatusUpdatedAt = now };
            db.Inquiries.Add(inquiry);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Inquiry {InquiryId} received.", inquiry.Id);
            return Ok(new { received = true });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Inquiries_SubmissionId" })
        {
            return Ok(new { received = true });
        }
        catch (Exception ex) when (ex is DbUpdateException or NpgsqlException or TimeoutException)
        {
            logger.LogError("Inquiry persistence failed ({ErrorType}).", ex.GetType().Name);
            return StatusCode(503, new { message = "We couldn't save your inquiry. Please try again or email Jonathan directly." });
        }
    }
}
