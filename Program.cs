using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using HendersonSoftwareLabsAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
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

// --- Reverse-proxy awareness ----------------------------------------------------------------
// In production Caddy terminates TLS and forwards to this app over plain HTTP. Without honoring
// X-Forwarded-For every request appears to come from the proxy, so any per-IP logic (the rate
// limiter below) collapses into one bucket and every auth log line records Caddy's address
// instead of the client's. Trust the forwarded headers only from the proxy network(s) below,
// never unconditionally.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // One proxy hop (Caddy). Bump via ForwardedHeaders__ForwardLimit if another proxy (an ALB,
    // Cloudflare) is ever put in front - and add its egress range to KnownNetworks too.
    options.ForwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;
    options.KnownProxies.Clear();
    options.KnownNetworks.Clear();

    // Override in prod via ForwardedHeaders__KnownNetworks (comma/semicolon-separated CIDRs) with
    // the tightest range that covers the proxy. Default: loopback + the default Docker bridge
    // range, which is what Kestrel sees when Caddy proxies to a published container port.
    var configured = builder.Configuration["ForwardedHeaders:KnownNetworks"];
    var cidrs = string.IsNullOrWhiteSpace(configured)
        ? new[] { "127.0.0.0/8", "::1/128", "172.16.0.0/12" }
        : configured.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (var cidr in cidrs)
    {
        var slash = cidr.IndexOf('/');
        if (slash > 0
            && IPAddress.TryParse(cidr[..slash], out var prefix)
            && int.TryParse(cidr[(slash + 1)..], out var length)
            && length >= 0
            && length <= (prefix.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32))
        {
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
        }
        else
        {
            Console.Error.WriteLine($"[startup] Ignoring malformed ForwardedHeaders:KnownNetworks entry '{cidr}'.");
        }
    }
});

// The JWT signing key is the whole strength of HS256 auth - a missing or weak value lets anyone
// forge a token, an admin one included. Fail fast rather than boot without one.
// Wave 2: also enforce a >= 32-byte minimum, once the prod Jwt__Key value is confirmed / rotated.
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"];
if (string.IsNullOrEmpty(jwtKey))
{
    throw new InvalidOperationException(
        "Jwt:Key is not configured. Supply a random 32+ byte value via the Jwt__Key environment " +
        "variable (generate one with `openssl rand -base64 48`).");
}

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
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
builder.Services.AddSingleton<ILineTicketService, LineTicketService>();

builder.Services.AddAuthorization(options =>
{
    // Fail closed: a controller/endpoint that carries no [Authorize]/[AllowAnonymous] now
    // requires an authenticated user rather than being reachable anonymously. The explicit
    // [AllowAnonymous] on AuthController.Login still wins.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Request throttling. UseForwardedHeaders runs first in the pipeline, so every partition here
// keys off the real client IP, not Caddy's.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Blanket per-IP backstop for every endpoint. Generous enough that an office behind one NAT
    // address doing normal portal/admin work never trips it; low enough to stop a scripted flood.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Login: a per-IP request cap on top of Identity's per-account lockout (which owns the 423
    // semantics). Stops rapid scripted guessing across many accounts before it reaches the
    // handler; loose enough for a morning login rush from one office IP.
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // The homepage Line's clear-flush endpoint, the only anonymous write surface here. A visitor
    // batches their clicks and flushes a handful of times in a normal session, more in a long one,
    // and a shared office address multiplies that. Generous on purpose: a rejected flush costs the
    // visitor nothing (their own backlog already cleared in the browser), and the real abuse
    // ceiling is the per-request clamp and the per-IP daily budget in LineController, not this.
    options.AddPolicy("line-clears", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0
            }));

    options.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Too many requests. Slow down and try again shortly." }, ct);
    };
});

// Cors:AllowedOrigin (env var Cors__AllowedOrigin) is the deployed SPA origin(s) the API
// allows - a comma- or semicolon-separated list, since the site is reachable at both the apex
// and the www host. Falls back to the local Vite dev origin so appsettings.Development.json
// doesn't need to duplicate it. Outside Development a missing value would silently lock the
// deployed UI out of the API with no obvious cause, so fail fast instead - same pattern as the
// Jwt:Key check above.
var configuredOrigins = builder.Configuration["Cors:AllowedOrigin"];
if (string.IsNullOrWhiteSpace(configuredOrigins) && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Cors:AllowedOrigin must be set outside Development (env Cors__AllowedOrigin) - " +
        "the deployed SPA origin(s) the API allows, comma-separated.");
}
var allowedOrigins = string.IsNullOrWhiteSpace(configuredOrigins)
    ? new[] { "http://localhost:5173" }
    : configuredOrigins.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options =>
{
    options.AddPolicy(AppCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Backs the homepage Line's GET response cache. That endpoint is anonymous, is hit by every
// visitor to the marketing site, and its answer changes only when someone adds a station, so a
// short in-process cache keeps a traffic spike off RDS entirely.
//
// SizeLimit is the guardrail, not a performance tweak. LineController also keeps a per-IP daily
// clear budget in here, keyed on the raw client address with a 24 hour TTL, so the key space is
// chosen by anonymous traffic: without a cap, a crawl spread across many source addresses grows
// this cache unbounded on a 1 GB box that also hosts a second app. Every entry declares Size = 1,
// so this is a ceiling on entry COUNT and the cache evicts the coldest entries once it is hit
// rather than consuming the instance. Losing an evicted budget entry costs nothing: the visitor
// simply gets a fresh daily allowance, which the per-request clamp still bounds.
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 20_000;
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

// --- HTTP pipeline ------------------------------------------------------------------------
// Must run before anything that reads the client address or scheme (rate limiter, auth, the
// exception handler's logging): rewrites them from Caddy's forwarded headers.
app.UseForwardedHeaders();

// Baseline security headers on every response. No HSTS / HTTPS redirect here - Caddy terminates
// TLS in front and the container only speaks HTTP; Caddy owns HSTS. The API serves JSON only,
// so no CSP: nosniff + frame-deny + no-referrer are the relevant ones.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseRateLimiter();

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
