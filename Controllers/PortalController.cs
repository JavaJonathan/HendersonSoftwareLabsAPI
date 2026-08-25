using System.Security.Claims;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Controllers;

[ApiController]
[Route("api/portal")]
[Authorize]
public class PortalController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public PortalController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet("projects")]
    public async Task<ActionResult<List<ProjectModel>>> GetMyProjects()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var projects = await _db.SoftwareProjects
            .Where(p => p.ClientUserId == userId)
            .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Select(ProjectModel.FromEntity)
            .ToListAsync();

        return Ok(projects);
    }
}
