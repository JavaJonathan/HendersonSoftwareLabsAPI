using System.Security.Claims;
using System.Text;
using HendersonSoftwareLabsAPI.Data;
using Microsoft.AspNetCore.Diagnostics;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

const string AppCorsPolicy = "AppCorsPolicy";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

var jwtSection = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        var key = jwtSection["Key"];
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = string.IsNullOrEmpty(key)
                ? null
                : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
        };

        // Bearer JWTs are stateless, so without this a password reset, lockout, or role change
        // has no effect on a token already issued until it naturally expires. Comparing against
        // the user's current SecurityStamp (rotated by Identity on password change) makes those
        // actions revoke access immediately instead of waiting out the token's lifetime.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var tokenStamp = context.Principal?.FindFirstValue(JwtTokenService.SecurityStampClaimType);

                var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var user = userId is null ? null : await userManager.FindByIdAsync(userId);

                if (user is null || user.SecurityStamp != tokenStamp)
                {
                    context.Fail("Token is no longer valid.");
                }
            }
        };
    });

builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

// Falls back to the local Vite dev origin so `appsettings.Development.json` doesn't need to
// duplicate it; production sets Cors:AllowedOrigin (env var Cors__AllowedOrigin) to the real
// deployed frontend URL.
var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173";
builder.Services.AddCors(options =>
{
    options.AddPolicy(AppCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Henderson Software Labs API", Version = "v1" });

    var jwtScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter a valid JWT token: Bearer {token}"
    };
    options.AddSecurityDefinition("Bearer", jwtScheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

var app = builder.Build();

// Ensure the Admin/Client roles exist, and that every user has one. Idempotent, runs on
// every startup (including CLI invocations) so create-admin can rely on roles already
// being present, and so any user that somehow ends up with no role self-heals to Client
// (the identity used to be inferred as "not an Admin" instead of an explicit role).
using (var roleSeedScope = app.Services.CreateScope())
{
    var services = roleSeedScope.ServiceProvider;
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

    foreach (var role in new[] { Roles.Admin, Roles.Client })
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
        }
    }

    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var db = services.GetRequiredService<ApplicationDbContext>();
    var users = await db.Users.ToListAsync();
    foreach (var user in users)
    {
        var roles = await userManager.GetRolesAsync(user);
        if (roles.Count == 0)
        {
            await userManager.AddToRoleAsync(user, Roles.Client);
        }
    }
}

// Command-mode branch: one-time bootstrap for the first admin account.
// Client accounts and their software are managed through the /admin UI once an admin exists.
// Usage:
//   dotnet run -- create-admin <email> <password>
if (args.Length > 0 && args[0] == "create-admin")
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;

    if (args.Length < 3)
    {
        Console.WriteLine("Usage: dotnet run -- create-admin <email> <password>");
        return;
    }

    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var email = args[1];
    var password = args[2];

    var user = new ApplicationUser
    {
        UserName = email,
        Email = email,
        CompanyName = "Henderson Software Labs",
        EmailConfirmed = true
    };

    var result = await userManager.CreateAsync(user, password);
    if (result.Succeeded)
    {
        await userManager.AddToRoleAsync(user, Roles.Admin);
        Console.WriteLine($"Created admin user '{email}' (id: {user.Id}).");
    }
    else
    {
        Console.WriteLine("Failed to create admin user:");
        foreach (var error in result.Errors)
        {
            Console.WriteLine($"  - {error.Code}: {error.Description}");
        }
    }
    return;
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        if (exception is not null)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalExceptionHandler");
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { message = "An unexpected error occurred." });
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors(AppCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
