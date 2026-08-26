using System.Security.Claims;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Models;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace HendersonSoftwareLabsAPI.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IJwtTokenService jwtTokenService,
        ILogger<AuthController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtTokenService = jwtTokenService;
        _logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponseModel>> Login(LoginRequestModel request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            _logger.LogWarning("Login failed for {Email}: no account with that email.", request.Email);
            return Unauthorized(new { message = "Invalid email or password." });
        }

        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            _logger.LogWarning("Login blocked for {UserId} ({Email}): account is locked out.", user.Id, request.Email);
            return StatusCode(StatusCodes.Status423Locked,
                new { message = "Too many failed login attempts. Please try again in 15 minutes." });
        }

        if (!result.Succeeded)
        {
            _logger.LogWarning("Login failed for {UserId} ({Email}): invalid password.", user.Id, request.Email);
            return Unauthorized(new { message = "Invalid email or password." });
        }

        var roles = await _userManager.GetRolesAsync(user);
        var (token, expiresAtUtc) = _jwtTokenService.CreateToken(user, roles);

        _logger.LogInformation("Login succeeded for {UserId} ({Email}), isAdmin={IsAdmin}.", user.Id, request.Email, Roles.IsAdmin(roles));

        return Ok(new LoginResponseModel
        {
            Token = token,
            ExpiresAtUtc = expiresAtUtc,
            Email = user.Email ?? string.Empty,
            CompanyName = user.CompanyName,
            IsAdmin = Roles.IsAdmin(roles)
        });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeResponseModel>> Me()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        var roles = await _userManager.GetRolesAsync(user);

        return Ok(new MeResponseModel
        {
            Email = user.Email ?? string.Empty,
            CompanyName = user.CompanyName,
            ContactName = user.ContactName,
            IsAdmin = Roles.IsAdmin(roles)
        });
    }
}
