using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Dtos;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _db;

    public AdminController(UserManager<ApplicationUser> userManager, ApplicationDbContext db)
    {
        _userManager = userManager;
        _db = db;
    }

    [HttpPost("clients")]
    public async Task<ActionResult<CreateClientResponseDto>> CreateClient(CreateClientRequestDto request)
    {
        var password = PasswordGenerator.Generate();

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            CompanyName = request.CompanyName,
            ContactName = request.ContactName,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            return BadRequest(new { message = string.Join("; ", result.Errors.Select(e => e.Description)) });
        }

        return Ok(new CreateClientResponseDto
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            CompanyName = user.CompanyName,
            GeneratedPassword = password
        });
    }

    [HttpGet("clients")]
    public async Task<ActionResult<List<AdminClientListItemDto>>> GetClients()
    {
        var adminUserIds = await _userManager.GetUsersInRoleAsync("Admin");
        var adminIdSet = adminUserIds.Select(u => u.Id).ToHashSet();

        var clients = await _db.Users
            .Where(u => !adminIdSet.Contains(u.Id))
            .Select(u => new AdminClientListItemDto
            {
                Id = u.Id,
                Email = u.Email ?? string.Empty,
                CompanyName = u.CompanyName,
                ContactName = u.ContactName,
                ProjectCount = u.SoftwareProjects.Count
            })
            .ToListAsync();

        return Ok(clients);
    }

    [HttpGet("clients/{clientId}/projects")]
    public async Task<ActionResult<List<ProjectDto>>> GetClientProjects(string clientId)
    {
        var clientExists = await _db.Users.AnyAsync(u => u.Id == clientId);
        if (!clientExists)
        {
            return NotFound(new { message = "Client not found." });
        }

        var projects = await _db.SoftwareProjects
            .Where(p => p.ClientUserId == clientId)
            .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Select(p => new ProjectDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                Status = p.Status.ToString(),
                Url = p.Url,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            })
            .ToListAsync();

        return Ok(projects);
    }

    [HttpPost("clients/{clientId}/projects")]
    public async Task<ActionResult<ProjectDto>> CreateProject(string clientId, CreateProjectRequestDto request)
    {
        var client = await _userManager.FindByIdAsync(clientId);
        if (client is null)
        {
            return NotFound(new { message = "Client not found." });
        }

        if (!Enum.TryParse<ProjectStatus>(request.Status, ignoreCase: true, out var status))
        {
            return BadRequest(new { message = $"Invalid status '{request.Status}'." });
        }

        var project = new SoftwareProject
        {
            Name = request.Name,
            Description = request.Description,
            Status = status,
            Url = request.Url,
            ClientUserId = client.Id,
            CreatedAt = DateTime.UtcNow
        };

        _db.SoftwareProjects.Add(project);
        await _db.SaveChangesAsync();

        return Ok(new ProjectDto
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            Status = project.Status.ToString(),
            Url = project.Url,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt
        });
    }
}
