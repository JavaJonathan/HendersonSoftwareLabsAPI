using System.Text;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

const string DevCorsPolicy = "DevCorsPolicy";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
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
    });

builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(DevCorsPolicy, policy =>
    {
        policy.WithOrigins("http://localhost:5173")
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

// Command-mode branch: manual client/project provisioning without an admin UI.
// Usage:
//   dotnet run -- provision-client <email> <password> <companyName>
//   dotnet run -- add-project <clientEmail> <name> <description> <status> [url]
if (args.Length > 0 && args[0] is "provision-client" or "add-project")
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;

    if (args[0] == "provision-client")
    {
        if (args.Length < 4)
        {
            Console.WriteLine("Usage: dotnet run -- provision-client <email> <password> <companyName>");
            return;
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var email = args[1];
        var password = args[2];
        var companyName = args[3];

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            CompanyName = companyName,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (result.Succeeded)
        {
            Console.WriteLine($"Created client user '{email}' (id: {user.Id}) for company '{companyName}'.");
        }
        else
        {
            Console.WriteLine("Failed to create client user:");
            foreach (var error in result.Errors)
            {
                Console.WriteLine($"  - {error.Code}: {error.Description}");
            }
        }
        return;
    }

    if (args[0] == "add-project")
    {
        if (args.Length < 5)
        {
            Console.WriteLine("Usage: dotnet run -- add-project <clientEmail> <name> <description> <status> [url]");
            Console.WriteLine("Status values: Planning, InProgress, Live, Maintenance, OnHold, Completed");
            return;
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var db = services.GetRequiredService<ApplicationDbContext>();

        var clientEmail = args[1];
        var name = args[2];
        var description = args[3];
        var statusArg = args[4];
        var url = args.Length > 5 ? args[5] : null;

        var user = await userManager.FindByEmailAsync(clientEmail);
        if (user is null)
        {
            Console.WriteLine($"No client user found with email '{clientEmail}'. Provision the client first.");
            return;
        }

        if (!Enum.TryParse<ProjectStatus>(statusArg, ignoreCase: true, out var status))
        {
            Console.WriteLine($"Invalid status '{statusArg}'. Valid values: Planning, InProgress, Live, Maintenance, OnHold, Completed");
            return;
        }

        var project = new SoftwareProject
        {
            Name = name,
            Description = description,
            Status = status,
            Url = url,
            ClientUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };

        db.SoftwareProjects.Add(project);
        await db.SaveChangesAsync();

        Console.WriteLine($"Added project '{name}' (id: {project.Id}) for client '{clientEmail}'.");
        return;
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors(DevCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
