using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Controllers;

[ApiController, Route("api/admin/inquiries"), Authorize(Roles = Roles.Admin)]
public class AdminInquiriesController(ApplicationDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string status = "New", int page = 1, CancellationToken ct = default)
    {
        if (page < 1 || page > 1000000) return BadRequest(new { message = "Invalid page." });
        var query = db.Inquiries.AsNoTracking();
        if (status != "All")
        {
            if (!TryStatus(status, out var parsed)) return BadRequest(new { message = "Invalid status." });
            query = query.Where(x => x.Status == parsed);
        }
        var total = await query.CountAsync(ct);
        var newCount = await db.Inquiries.CountAsync(x => x.Status == InquiryStatus.New, ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * 25).Take(25)
            .Select(x => new { x.Id, x.Name, x.Email, x.CreatedAt, Status = x.Status.ToString(), Preview = x.Message.Substring(0, Math.Min(x.Message.Length, 160)) }).ToListAsync(ct);
        return Ok(new { items, total, newCount, page, pageSize = 25 });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var x = await db.Inquiries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return x is null ? NotFound() : Ok(new { x.Id, x.Name, x.Email, x.Message, x.CreatedAt, Status = x.Status.ToString(), x.StatusUpdatedAt });
    }

    public record StatusRequest(string Status);

    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> Update(int id, StatusRequest request, CancellationToken ct)
    {
        if (!TryStatus(request.Status, out var status)) return BadRequest(new { message = "Invalid status." });
        var inquiry = await db.Inquiries.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (inquiry is null) return NotFound();
        if (inquiry.Status != status)
        {
            inquiry.Status = status;
            inquiry.StatusUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    private static bool TryStatus(string value, out InquiryStatus status) =>
        Enum.TryParse(value, out status) && Enum.IsDefined(status) && status.ToString() == value;
}
