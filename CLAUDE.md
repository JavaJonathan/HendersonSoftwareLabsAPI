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

**Role model**: `Entities/Roles.cs` defines two explicit Identity roles, `Admin` and `Client` — every `ApplicationUser` has exactly one. `Program.cs` seeds both roles on every startup and self-heals any pre-existing user with zero roles to `Client`. `JwtTokenService.CreateToken` embeds one `ClaimTypes.Role` claim per role the user has, plus a `security_stamp` claim checked on every request (see below), which is what makes `[Authorize(Roles = Roles.Admin)]` on `AdminController` work with no extra JWT config.

**Client provisioning is admin-UI-only, not CLI or self-service.** An earlier version of this project had `provision-client`/`add-project` CLI commands — those were deliberately removed once `AdminController` covered the same ground. Client accounts and their software are created exclusively through the authenticated `/api/admin/*` endpoints (consumed by the React app's `/admin` area). **Clients never choose their own password** — `Services/PasswordGenerator.cs` generates a cryptographically random one (`RandomNumberGenerator`, not `System.Random`) server-side in `AdminController.CreateClient`, and it's returned exactly once in the HTTP response for the admin to relay manually; it is never logged or persisted anywhere except as Identity's one-way password hash.

**Auth flow**: `AuthController.Login` uses `SignInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)` — not a raw `UserManager.CheckPasswordAsync` call — specifically so failed-login lockout (`options.Lockout.*` in `Program.cs`, 5 attempts / 15 min) is enforced. A locked-out account gets `423 Locked` with a distinct message; unknown email or wrong password both get a generic `401` to avoid leaking account existence on a single guess. Do not swap this back to `CheckPasswordAsync` — that bypasses lockout entirely. `SignInManager<ApplicationUser>` needs no separate DI registration; `AddIdentity` already provides it.

**Data model**: one `ApplicationUser` (`Entities/ApplicationUser.cs`, extends `IdentityUser` with `CompanyName`/`ContactName`) has many `SoftwareProject` (`Entities/SoftwareProject.cs`) via `ClientUserId`. `PortalController.GetMyProjects` filters strictly by the caller's own `NameIdentifier` claim — a client can only ever see their own projects, never another client's, and there is no endpoint that lets a client query anyone else's data. `AdminController` is the only place that can query/create across all clients, and it's gated by the `Admin` role.

**Three controllers, three trust levels**:
- `AuthController` — `[AllowAnonymous]` login, `[Authorize]` `/me` (any authenticated user)
- `PortalController` — `[Authorize]`, scoped to the caller's own data only
- `AdminController` — `[Authorize(Roles = "Admin")]` on the whole controller

**CORS** is locked to the Vite dev origin (`http://localhost:5173`) via a named policy — update `Program.cs` if the frontend's dev port ever changes (it's pinned with `strictPort` on the UI side for exactly this reason).

## Production Deployment

**Live**: API at `https://api.hendersonsoftwarelabs.com`. Frontend is a separate repo (`HendersonSoftwareLabsUI`) deployed on AWS Amplify at `https://hendersonsoftwarelabs.com` — see that repo's `CLAUDE.md` for its side.

**Infrastructure** (AWS account `441627938519`, region `us-east-1`):
- **Compute**: one EC2 instance (`i-076d8b6b1463968a1`, `t3.micro`) running the API in Docker, with **Caddy** as a reverse proxy in front of it handling automatic HTTPS (Let's Encrypt) for the `api.` domain. Has an Elastic IP (`100.57.201.123`) attached so the domain/cert survive a reboot — without it, a stopped/restarted instance gets a new public IP and both the DNS record and the cert break.
- **Database**: RDS PostgreSQL (`hsl-postgres`, `db.t4g.micro`) — not publicly accessible, reachable only from the EC2 instance's security group.
- **Container registry**: ECR repo `henderson-software-labs-api`.
- **Secrets**: SSM Parameter Store, `/hsl/prod/ConnectionStrings/Default` and `/hsl/prod/Jwt/Key` (both `SecureString`) — read by the instance and written to `/etc/hsl-api.env`, which the container is started with via `--env-file`.
- **Deploy files in this repo**: `Dockerfile`, `Caddyfile`, `.github/workflows/deploy-api.yml`.

**CI/CD**: pushing to `master` triggers `.github/workflows/deploy-api.yml` — builds the image, pushes to ECR, then uses an **AWS Systems Manager (SSM) Run Command** to have the instance pull the new image and restart the container. No SSH — port 22 isn't even open on the instance's security group. GitHub authenticates to AWS via **OIDC** (IAM role `hsl-github-actions-deploy`), not stored access keys.

**Creating an admin account in production** (mirrors the local `create-admin` bootstrap, just run inside the live container via SSM instead of `dotnet run --`):
```bash
aws ssm send-command --instance-ids i-076d8b6b1463968a1 --document-name "AWS-RunShellScript" \
  --parameters 'commands=["docker exec hsl-api dotnet HendersonSoftwareLabsAPI.dll create-admin <email> <password>"]'
```

**Applying EF Core migrations to production** — RDS isn't publicly reachable, so `dotnet ef database update` can't run from a dev machine directly. Build a self-contained migrations bundle and run it from the EC2 instance instead (it has private network access to RDS):
```bash
dotnet ef migrations bundle --self-contained -r linux-x64 -o efbundle
# upload efbundle to the instance (e.g. via a temporary S3 object), then on the instance:
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ./efbundle --connection "<connection string>"
```
(`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` is required — this minimal Amazon Linux 2023 AMI has no ICU package, and the bundle otherwise aborts immediately with a missing-libicu error.)

## Known deployment gotchas (from setting this up)

- **AWS App Runner stopped accepting new customers as of April 30, 2026** (now in maintenance mode); its replacement is Amazon ECS Express Mode. Neither ended up used here — this runs on a plain EC2 instance instead, for cost reasons (no ALB, no managed-container premium).
- **Let's Encrypt refuses to issue certificates for `*.amazonaws.com` hostnames** (anti-abuse policy) — Caddy's automatic HTTPS cannot work against an EC2 instance's default public DNS name. A real domain is required (a free IP-encoding service like `sslip.io` works as a stopgap before one is ready).
- **GitHub Actions' OIDC `sub` claim is not the commonly-documented `repo:OWNER/REPO:ref:refs/heads/BRANCH` format** — it embeds immutable numeric IDs: `repo:OWNER@<ownerId>/REPO@<repoId>:ref:refs/heads/BRANCH`. An IAM trust policy written against the old format rejects every request with a generic "Not authorized to perform sts:AssumeRoleWithWebIdentity" and no further detail. Check the real value via CloudTrail (`aws cloudtrail lookup-events --lookup-attributes AttributeKey=EventName,AttributeValue=AssumeRoleWithWebIdentity`) rather than assuming the documented format, and use a `StringLike` condition with a wildcard for the numeric ID segments.
- **`git push` to `.github/workflows/*.yml` is blocked** ("refusing to allow an OAuth App to create or update workflow ... without `workflow` scope") for git clients whose cached credential lacks that OAuth scope. Add/edit workflow files directly via the GitHub web UI instead (commit straight to the branch — watch for the wizard defaulting to "create a new branch" instead).
- **The EC2 instance's `/tmp` is a small tmpfs (~456MB)** on this AMI, not disk-backed — downloading anything sizable there (the AWS CLI installer, a migrations bundle) can fail with "No space left on device" even though the root volume has plenty of room. Use `/opt` or another disk-backed path instead.
- **RDS on a free-tier account caps the backup retention period** — `create-db-instance --backup-retention-period` above a small threshold fails with `FreeTierRestrictionError`; use `1`.

## Known gotchas (from this project's history)

- `dotnet add package` with no `--version` grabs the newest release, which may target a newer TFM than this project's `net9.0` and fail to restore (bit this project with EF Core/Identity/JwtBearer, and again with `Swashbuckle.AspNetCore` pulling in a `Microsoft.OpenApi` v2 with breaking namespace changes). Always pin to the current `9.0.x` line explicitly when adding a Microsoft.* or EF Core package; `Swashbuckle.AspNetCore` specifically must stay on `9.0.6`, not `10.x`.
- If `dotnet build`/`dotnet run` fails with an MSB3027 "file is locked by another process" error, a previous `dotnet run` is still holding the output DLL — find and kill it (`Get-NetTCPConnection -LocalPort 5194` in PowerShell) rather than fighting the build.
