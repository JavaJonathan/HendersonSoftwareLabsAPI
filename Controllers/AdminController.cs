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
[Authorize(Roles = Roles.Admin)]
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

        await _userManager.AddToRoleAsync(user, Roles.Client);

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
        var clients = await ClientsQuery().ToListAsync();
        return Ok(clients);
    }

    [HttpGet("clients/{clientId}")]
    public async Task<ActionResult<AdminClientListItemDto>> GetClient(string clientId)
    {
        var client = await ClientsQuery().FirstOrDefaultAsync(c => c.Id == clientId);
        if (client is null)
        {
            return NotFound(new { message = "Client not found." });
        }

        return Ok(client);
    }

    private IQueryable<AdminClientListItemDto> ClientsQuery()
    {
        var clientIds = _db.UserRoles
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .Where(x => x.Name == Roles.Client)
            .Select(x => x.UserId);

        return _db.Users
            .Where(u => clientIds.Contains(u.Id))
            .Select(u => new AdminClientListItemDto
            {
                Id = u.Id,
                Email = u.Email ?? string.Empty,
                CompanyName = u.CompanyName,
                ContactName = u.ContactName,
                ProjectCount = u.SoftwareProjects.Count
            });
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
            .Select(ProjectDto.FromEntity)
            .ToListAsync();

        return Ok(projects);
    }

    [HttpPost("clients/{clientId}/projects")]
    public async Task<ActionResult<ProjectDto>> CreateProject(string clientId, CreateProjectRequestDto request)
    {
        var clientExists = await _db.Users.AnyAsync(u => u.Id == clientId);
        if (!clientExists)
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
            ClientUserId = clientId,
            CreatedAt = DateTime.UtcNow
        };

        _db.SoftwareProjects.Add(project);
        await _db.SaveChangesAsync();

        return Ok(ProjectDto.FromEntity.Compile()(project));
    }
}
