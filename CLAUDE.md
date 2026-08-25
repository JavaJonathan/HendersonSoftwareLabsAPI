# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                          # build
dotnet run                            # run the API (Development profile, http://localhost:5194)
dotnet ef migrations add <Name>       # add a migration after changing an entity or ApplicationDbContext
dotnet ef database update             # apply pending migrations to the local Postgres DB
```

There is no automated test suite in this project.

**Local dev prerequisites**: a running local PostgreSQL instance, and two values in `dotnet user-secrets` (never committed — `appsettings.json` only has placeholders):

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=HendersonSoftwareLabs;Username=postgres;Password=<yours>"
dotnet user-secrets set "Jwt:Key" "<a random 32+ byte string>"
```

**Bootstrapping the first admin account** — the only CLI provisioning command left in this project (see Architecture below):

```bash
dotnet run -- create-admin <email> <password>
```

## Architecture

**Stack**: ASP.NET Core 9 Web API, EF Core with `Npgsql.EntityFrameworkCore.PostgreSQL`, ASP.NET Core Identity for auth, stateless JWT bearer tokens (no cookies, no server-side sessions).

**`Program.cs` is the composition root and does more than typical minimal-API setup** — worth reading in full before making auth/startup changes. In order: DI/Identity/JWT/CORS/Swagger registration, then (after `app.Build()`) idempotent "Admin" role seeding that runs on *every* startup including CLI invocations, then a command-mode branch that intercepts `args[0] == "create-admin"` and exits before `app.Run()` is ever reached. This CLI branch is the only way to create the first admin account — there is no other bootstrap path.

**Role model**: `"Admin"` is the only explicit Identity role. Every other `ApplicationUser` is implicitly a client — there is no `"Client"` role. `JwtTokenService.CreateToken` embeds one `ClaimTypes.Role` claim per role the user has, which is what makes `[Authorize(Roles = "Admin")]` on `AdminController` work with no extra JWT config.

**Client provisioning is admin-UI-only, not CLI or self-service.** An earlier version of this project had `provision-client`/`add-project` CLI commands — those were deliberately removed once `AdminController` covered the same ground. Client accounts and their software are created exclusively through the authenticated `/api/admin/*` endpoints (consumed by the React app's `/admin` area). **Clients never choose their own password** — `Services/PasswordGenerator.cs` generates a cryptographically random one (`RandomNumberGenerator`, not `System.Random`) server-side in `AdminController.CreateClient`, and it's returned exactly once in the HTTP response for the admin to relay manually; it is never logged or persisted anywhere except as Identity's one-way password hash.

**Auth flow**: `AuthController.Login` uses `SignInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)` — not a raw `UserManager.CheckPasswordAsync` call — specifically so failed-login lockout (`options.Lockout.*` in `Program.cs`, 5 attempts / 15 min) is enforced. A locked-out account gets `423 Locked` with a distinct message; unknown email or wrong password both get a generic `401` to avoid leaking account existence on a single guess. Do not swap this back to `CheckPasswordAsync` — that bypasses lockout entirely. `SignInManager<ApplicationUser>` needs no separate DI registration; `AddIdentity` already provides it.

**Data model**: one `ApplicationUser` (`Entities/ApplicationUser.cs`, extends `IdentityUser` with `CompanyName`/`ContactName`) has many `SoftwareProject` (`Entities/SoftwareProject.cs`) via `ClientUserId`. `PortalController.GetMyProjects` filters strictly by the caller's own `NameIdentifier` claim — a client can only ever see their own projects, never another client's, and there is no endpoint that lets a client query anyone else's data. `AdminController` is the only place that can query/create across all clients, and it's gated by the `Admin` role.

**Three controllers, three trust levels**:
- `AuthController` — `[AllowAnonymous]` login, `[Authorize]` `/me` (any authenticated user)
- `PortalController` — `[Authorize]`, scoped to the caller's own data only
- `AdminController` — `[Authorize(Roles = "Admin")]` on the whole controller

**CORS** is locked to the Vite dev origin (`http://localhost:5173`) via a named policy — update `Program.cs` if the frontend's dev port ever changes (it's pinned with `strictPort` on the UI side for exactly this reason).

## Known gotchas (from this project's history)

- `dotnet add package` with no `--version` grabs the newest release, which may target a newer TFM than this project's `net9.0` and fail to restore (bit this project with EF Core/Identity/JwtBearer, and again with `Swashbuckle.AspNetCore` pulling in a `Microsoft.OpenApi` v2 with breaking namespace changes). Always pin to the current `9.0.x` line explicitly when adding a Microsoft.* or EF Core package; `Swashbuckle.AspNetCore` specifically must stay on `9.0.6`, not `10.x`.
- If `dotnet build`/`dotnet run` fails with an MSB3027 "file is locked by another process" error, a previous `dotnet run` is still holding the output DLL — find and kill it (`Get-NetTCPConnection -LocalPort 5194` in PowerShell) rather than fighting the build.
