# AGENTS.md

This file provides guidance to AI coding agents when working with code in this repository. `CLAUDE.md` in this repo mirrors this file for Claude Code - update both together.

## Writing style

Do not use em dashes (`—`) anywhere: not in code, comments, docs, commit messages, or user-facing copy. Rewrite with a comma, parentheses, a colon, or two sentences; a spaced hyphen (` - `) is an acceptable last resort. This applies to en dashes (`–`) in prose too; a plain hyphen is fine for ranges.

## Project Status (as of 2026-09-22)

Opportunity Radar (commit `252bedf`, all checkpoints through "review fixes" below) is committed, pushed to `master`, and deployed to production via the standard push-triggered pipeline (see "Production Deployment"); the `InitOpportunityRadar`/`AddOpportunityExternalIdIndex` migrations have been applied to the production database (confirmed by Jonathan, 2026-09-22). The UI's corresponding "decision cockpit redesign" (see the UI repo's `AGENTS.md`) is likewise committed and deployed. Live Jev is now usable in production: the `TypeSafe/ApiKey` was added to SSM Parameter Store at `/hsl/prod/TypeSafe/ApiKey`, written into `/etc/hsl-api.env` on the EC2 instance, and the `hsl-api` container was restarted to pick it up (all 2026-09-22, verified via both an internal and external health check). Everything else is deployed and live in production with no known bugs or unfinished work. Next up: the shared "Line" homepage game: `LineController` (see its own doc comment for the anonymous-write design rationale), the `LineKindProgress` entity/migration, and the admin reset endpoint on `AdminController` - see "Three controllers, three trust levels" below for where it fits. This file and the UI repo's `AGENTS.md` are both kept current - read both before resuming.

## Commands

```bash
dotnet build                          # build
dotnet run                            # run the API (Development profile, http://localhost:5194)
dotnet ef migrations add <Name>       # add a migration after changing an entity or ApplicationDbContext
dotnet ef database update             # apply pending migrations to the local Postgres DB
```

The inquiry integration harness is documented under Contact inquiries below.

**Local dev prerequisites**: a running local PostgreSQL instance, and two values in `dotnet user-secrets` (never committed - `appsettings.json` only has placeholders):

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=HendersonSoftwareLabs;Username=postgres;Password=<yours>"
dotnet user-secrets set "Jwt:Key" "<a random 32+ byte string>"
```

**Bootstrapping the first admin account** - the only CLI provisioning command left in this project (see Architecture below):

```bash
dotnet run -- create-admin <email> <password>
```

## Architecture

**Stack**: ASP.NET Core 9 Web API, EF Core with `Npgsql.EntityFrameworkCore.PostgreSQL`, ASP.NET Core Identity for auth, stateless JWT bearer tokens (no cookies, no server-side sessions).

**`Program.cs` is the composition root and does more than typical minimal-API setup** - worth reading in full before making auth/startup changes. In order: DI/Identity/JWT/CORS/Swagger registration, then (after `app.Build()`) idempotent "Admin" role seeding that runs on *every* startup including CLI invocations, then a command-mode branch that intercepts `args[0] == "create-admin"` and exits before `app.Run()` is ever reached. This CLI branch is the only way to create the first admin account - there is no other bootstrap path.

**Role model**: `Entities/Roles.cs` defines two explicit Identity roles, `Admin` and `Client` - every `ApplicationUser` has exactly one. `Program.cs` seeds both roles on every startup and self-heals any pre-existing user with zero roles to `Client`. `JwtTokenService.CreateToken` embeds one `ClaimTypes.Role` claim per role the user has, plus a `security_stamp` claim checked on every request (see below), which is what makes `[Authorize(Roles = Roles.Admin)]` on `AdminController` work with no extra JWT config.

**Client provisioning is admin-UI-only, not CLI or self-service.** An earlier version of this project had `provision-client`/`add-project` CLI commands - those were deliberately removed once `AdminController` covered the same ground. Client accounts and their software are created exclusively through the authenticated `/api/admin/*` endpoints (consumed by the React app's `/admin` area). **Clients never choose their own password** - `Services/PasswordGenerator.cs` generates a cryptographically random one (`RandomNumberGenerator`, not `System.Random`) server-side in `AdminController.CreateClient`, and it's returned exactly once in the HTTP response for the admin to relay manually; it is never logged or persisted anywhere except as Identity's one-way password hash.

**Auth flow**: `AuthController.Login` uses `SignInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)` - not a raw `UserManager.CheckPasswordAsync` call - specifically so failed-login lockout (`options.Lockout.*` in `Program.cs`, 5 attempts / 15 min) is enforced. A locked-out account gets `423 Locked` with a distinct message; unknown email or wrong password both get a generic `401` to avoid leaking account existence on a single guess. Do not swap this back to `CheckPasswordAsync` - that bypasses lockout entirely. `SignInManager<ApplicationUser>` needs no separate DI registration; `AddIdentity` already provides it.

**Data model**: one `ApplicationUser` (`Entities/ApplicationUser.cs`, extends `IdentityUser` with `CompanyName`/`ContactName`) has many `SoftwareProject` (`Entities/SoftwareProject.cs`) via `ClientUserId`. `PortalController.GetMyProjects` filters strictly by the caller's own `NameIdentifier` claim - a client can only ever see their own projects, never another client's, and there is no endpoint that lets a client query anyone else's data. `AdminController` is the only place that can query/create across all clients, and it's gated by the `Admin` role.

**Controller authorization boundaries**:
- `AuthController` - `[AllowAnonymous]` login, `[Authorize]` `/me` (any authenticated user)
- `PortalController` - `[Authorize]`, scoped to the caller's own data only
- `AdminController` - `[Authorize(Roles = "Admin")]` on the whole controller
- `ContactController` - `[AllowAnonymous]`, validates and stores inquiries; `AdminInquiriesController` requires Admin for inbox reads and status changes.
- `LineController` - `[AllowAnonymous]` at class level, an anonymous *write* surface in the API (backs the homepage's shared "Line" game); see its own doc comment for why that's deliberate and how writes are bounded

**CORS** is locked to the Vite dev origin (`http://localhost:5173`) via a named policy - update `Program.cs` if the frontend's dev port ever changes (it's pinned with `strictPort` on the UI side for exactly this reason).

## Production Deployment

**Live**: API at `https://api.hendersonsoftwarelabs.com`. Frontend is a separate repo (`HendersonSoftwareLabsUI`) deployed on AWS Amplify at `https://hendersonsoftwarelabs.com` - see that repo's `AGENTS.md` for its side.

**Infrastructure** (AWS account `441627938519`, region `us-east-1`):
- **Compute**: one EC2 instance (`i-076d8b6b1463968a1`, `t3.micro`) running the API in Docker, with **Caddy** as a reverse proxy in front of it handling automatic HTTPS (Let's Encrypt) for the `api.` domain. Has an Elastic IP (`100.57.201.123`) attached so the domain/cert survive a reboot - without it, a stopped/restarted instance gets a new public IP and both the DNS record and the cert break.
- **Database**: RDS PostgreSQL (`hsl-postgres`, `db.t4g.micro`) - not publicly accessible, reachable only from the EC2 instance's security group.
- **Container registry**: ECR repo `henderson-software-labs-api`.
- **Secrets**: SSM Parameter Store, `/hsl/prod/ConnectionStrings/Default` and `/hsl/prod/Jwt/Key` (both `SecureString`) - read by the instance and written to `/etc/hsl-api.env`, which the container is started with via `--env-file`.
- **Deploy files in this repo**: `Dockerfile`, `Caddyfile`, `.github/workflows/deploy-api.yml`.

**CI/CD**: pushing to `master` triggers `.github/workflows/deploy-api.yml` - builds the image, pushes to ECR, then uses an **AWS Systems Manager (SSM) Run Command** to have the instance pull the new image and restart the container. No SSH - port 22 isn't even open on the instance's security group. GitHub authenticates to AWS via **OIDC** (IAM role `hsl-github-actions-deploy`), not stored access keys.

**Creating an admin account in production** (mirrors the local `create-admin` bootstrap, just run inside the live container via SSM instead of `dotnet run --`):
```bash
aws ssm send-command --instance-ids i-076d8b6b1463968a1 --document-name "AWS-RunShellScript" \
  --parameters 'commands=["docker exec hsl-api dotnet HendersonSoftwareLabsAPI.dll create-admin <email> <password>"]'
```

**Applying EF Core migrations to production** - RDS isn't publicly reachable, so `dotnet ef database update` can't run from a dev machine directly. Build a self-contained migrations bundle and run it from the EC2 instance instead (it has private network access to RDS):
```bash
dotnet ef migrations bundle --self-contained -r linux-x64 -o efbundle
# upload efbundle to the instance (e.g. via a temporary S3 object), then on the instance:
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ./efbundle --connection "<connection string>"
```
(`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` is required - this minimal Amazon Linux 2023 AMI has no ICU package, and the bundle otherwise aborts immediately with a missing-libicu error.)

## Known deployment gotchas (from setting this up)

- **AWS App Runner stopped accepting new customers as of April 30, 2026** (now in maintenance mode); its replacement is Amazon ECS Express Mode. Neither ended up used here - this runs on a plain EC2 instance instead, for cost reasons (no ALB, no managed-container premium).
- **Let's Encrypt refuses to issue certificates for `*.amazonaws.com` hostnames** (anti-abuse policy) - Caddy's automatic HTTPS cannot work against an EC2 instance's default public DNS name. A real domain is required (a free IP-encoding service like `sslip.io` works as a stopgap before one is ready).
- **GitHub Actions' OIDC `sub` claim is not the commonly-documented `repo:OWNER/REPO:ref:refs/heads/BRANCH` format** - it embeds immutable numeric IDs: `repo:OWNER@<ownerId>/REPO@<repoId>:ref:refs/heads/BRANCH`. An IAM trust policy written against the old format rejects every request with a generic "Not authorized to perform sts:AssumeRoleWithWebIdentity" and no further detail. Check the real value via CloudTrail (`aws cloudtrail lookup-events --lookup-attributes AttributeKey=EventName,AttributeValue=AssumeRoleWithWebIdentity`) rather than assuming the documented format, and use a `StringLike` condition with a wildcard for the numeric ID segments.
- **`git push` to `.github/workflows/*.yml` is blocked** ("refusing to allow an OAuth App to create or update workflow ... without `workflow` scope") for git clients whose cached credential lacks that OAuth scope. Add/edit workflow files directly via the GitHub web UI instead (commit straight to the branch - watch for the wizard defaulting to "create a new branch" instead).
- **The EC2 instance's `/tmp` is a small tmpfs (~456MB)** on this AMI, not disk-backed - downloading anything sizable there (the AWS CLI installer, a migrations bundle) can fail with "No space left on device" even though the root volume has plenty of room. Use `/opt` or another disk-backed path instead.
- **RDS on a free-tier account caps the backup retention period** - `create-db-instance --backup-retention-period` above a small threshold fails with `FreeTierRestrictionError`; use `1`.

## Known gotchas (from this project's history)

- `dotnet add package` with no `--version` grabs the newest release, which may target a newer TFM than this project's `net9.0` and fail to restore (bit this project with EF Core/Identity/JwtBearer, and again with `Swashbuckle.AspNetCore` pulling in a `Microsoft.OpenApi` v2 with breaking namespace changes). Always pin to the current `9.0.x` line explicitly when adding a Microsoft.* or EF Core package; `Swashbuckle.AspNetCore` specifically must stay on `9.0.6`, not `10.x`.
- If `dotnet build`/`dotnet run` fails with an MSB3027 "file is locked by another process" error, a previous `dotnet run` is still holding the output DLL - find and kill it (`Get-NetTCPConnection -LocalPort 5194` in PowerShell) rather than fighting the build.

## Contact inquiries (2026-09-19)

`Inquiry` records are independent of Identity users and are stored in PostgreSQL by the additive `AddInquiries` migration. `POST /api/contact` is an anonymous write endpoint with a 32 KB body limit, field validation, honeypot and three requests per IP per 15 minutes. Submission UUIDs have a unique index; retries acknowledge receipt without creating a duplicate. No inquiry data is returned publicly and no email is sent.

Admin-only `/api/admin/inquiries` endpoints list (status filter, 25-row pagination, new count), read details and PATCH `/{id}/status`. Status values are New, Contacted and Archived; timestamps are UTC. There is no deletion or automatic expiration. Do not log inquiry content or visitor contact details.

CORS wraps exception handling and rate limiting so the UI can read failure responses. Oversized request exceptions preserve their HTTP status. The design-time DbContext factory allows migrations without starting the web host or loading JWT configuration. It reads local user secrets and environment variables; never point tests at production.

Integration checks: build Release, then run `dotnet run --project tests/InquiryIntegration -c Release` from this repo. Set `ConnectionStrings__Default` to a localhost PostgreSQL administrator connection or configure local user secrets. The harness rejects remote hosts, creates a uniquely named disposable database, applies migrations, launches its own API, and removes its database afterward. It tests persistence, retries, validation, throttling, authorization, pagination, status changes and database failure.

Rollout: apply the migration bundle before deploying the API, then deploy the UI. Jonathan checks the inbox manually; no notifications or automatic replies exist.

## Opportunity Radar checkpoint 1 (2026-09-20)

Admin-only `/api/admin/opportunity-radar` endpoints back the private opportunity review workflow. `Opportunity`, `OpportunityEvaluation`, and `RadarPreferences` use PostgreSQL, with source passages and evaluation details stored as JSONB. Imported source text is limited to 30,000 characters per record; CSV imports are limited to 100 records and 2 MB. Source URLs are stored only and must be absolute HTTP(S) URLs. The server never fetches them.

Checkpoint 1 uses `OpportunityRadarEngine`, a deterministic simulated evaluator. Every simulated result must stay labeled as simulated. Missing budget is `Unknown`, not incompatible; the budget floor and scope window come only from each admin's persisted preferences. Exact duplicates are rejected and near-duplicates are linked for review.

Focused checks: `dotnet run --project tests/OpportunityRadar.Unit`. The `AddOpportunityRadar` migration is local only until Jonathan explicitly approves applying it.

## Opportunity Radar checkpoint 2 (2026-09-20)

`IOpportunityEvaluator` separates the deterministic simulated provider from the live `JevOpportunityEvaluator`. Jev uses the fixed TypeSafe `/v1/systemone` endpoint with focused Choice, Score, and Noul questions. The application keeps budget math, date handling, preference gates, evidence validation, ranking, and recommendation templates in code.

Live batches require a fresh server-generated preview confirmation. Defaults are a $0.05 estimated maximum batch cost, a rolling 5,000,000 input token limit, four concurrent calls, a 15 second timeout, and at most two retries for transport errors, 429, or 529. Provider failures are persisted as failures and never become Pass. Configure `TypeSafe:ApiKey` only through user secrets locally and `TypeSafe__ApiKey` from SSM in production. See `OPPORTUNITY-RADAR.md` for setup and the opt-in live contract command. The `AddJevEvaluationMetadata` migration remains local until Jonathan approves applying it.

## Opportunity Radar checkpoint 3 (2026-09-20)

`OpportunityRadarReporting` owns the inspectable literal keyword baseline and CSV generation. Baseline output includes matched terms, a coarse score, and hard-rule outcomes. Synthetic comparisons are illustrations, never benchmarks.

`GET /api/admin/opportunity-radar/export` excludes synthetic records unless `includeSynthetic=true`. It quotes every field, doubles embedded quotes, replaces control characters, and neutralizes leading ASCII and full-width formula characters without mutating stored data. Keep all imported and user-written fields on this sanitizer path. Focused tests cover formula prefixes, tabs, line breaks, quote breakout, Unicode variants, keyword misses, keyword false positives, and admin authorization.

## Opportunity Radar: split into ActiveProject / BusinessProspect (2026-09-21)

Checkpoints 1-3 above shipped a single opportunity model. Before any migration was ever applied, that model was split into two entity types sharing one `Opportunity` table plus type-specific detail tables (`ActiveProjectDetail`, `BusinessProspectDetail`, both keyed 1:1 on `OpportunityId`) - table-splitting, not EF inheritance, matching this codebase's flat-POCO convention everywhere else. `Opportunity.EntityType` (`ActiveProject`/`BusinessProspect`) is the discriminant; `OpportunitySourceType`/`OpportunityKind` were retired (`WebsiteOpportunity` *is* `EntityType.BusinessProspect` now).

- **`ActiveProject`**: today's explicit-demand model, functionally unchanged. `ActiveProjectDecision` (Pursue/Investigate/Pass). Keyword baseline stays ActiveProject-only, there is no literal-keyword equivalent for a business's web presence.
- **`BusinessProspect`**: an established business with an observable weak web presence. `BusinessProspectDecision` (Prioritize/Watch/Skip) - deliberately not Pursue/Investigate/Pass, since "Pursue" implies acting on inbound demand a cold prospect never has. Buying intent defaults to `Unknown` and is never inferred from a weak website alone; scored on business strength, digital-presence weakness, reputation-vs-website mismatch, entry-project strength, geography, contactability, evidence completeness (`OpportunityRadarEngine.EvaluateBusinessProspect`/`ComposeBusinessProspect`). Dedup is by business identity (`BusinessProspectDetail.NormalizedBusinessName` fuzzy match, `NormalizedWebsiteDomain` exact match), not the ActiveProject text-similarity fingerprint.
- `OpportunityEvaluation.Recommendation` widened to one shared `OpportunityRecommendation` enum (all 6 values); validity per record is enforced in app code against `Opportunity.EntityType`, same pattern as every other nullable enum here.
- `IOpportunityEvaluator` gained `SupportedEntityType`; four evaluators are registered (`SimulatedActiveProjectEvaluator`, `JevActiveProjectEvaluator`, `SimulatedBusinessProspectEvaluator`, `JevBusinessProspectEvaluator`, the Jev pair sharing retry/parsing plumbing via `JevEvaluatorBase`). The controller resolves `evaluators.Single(x => x.Provider == provider && x.SupportedEntityType == entityType)` per opportunity, so one batch can mix both types. `JevBusinessProspectEvaluator`'s request state is deliberately `business_name`/`industry`/`geography`/`passages` only, no preferences-derived fields, so no BusinessProspect preference change can ever mark a live result stale (verified by `EstimateMaximumInputTokens` being preference-independent).
- Ingestion stays CSV/paste only (no live external ingestion API) - `POST import/active-projects[/csv]` and `POST import/business-prospects[/csv]`, each request/response DTO and CSV column contract living in `Services/OpportunityImportService.cs` / `Services/OpportunityCsvImportService.cs`. Exact-match resubmission **upserts** the existing row's source material rather than rejecting it, and never touches `UserDecision`/`Notes`/`DuplicateOfId`/`IsSynthetic` - this is what makes safe CSV resubmission from an external research pass possible.
- New `GET /digest` returns today's top `DigestActiveProjectCount`/`DigestBusinessProspectCount` (from `RadarPreferences`, defaults 3/2) qualifying records per lane, ranked, never backfilled with a lower tier to fill the quota.
- `RadarPreferences` splits into `ActiveProjectPreferencesJson`/`BusinessProspectPreferencesJson` (both jsonb) plus the two digest counts; `OpportunityRadarEngine.ReadActiveProjectPreferences`/`ReadBusinessProspectPreferences` are the only readers, with safe defaults on parse failure.
- `RadarPassage`/`SourcePassagesJson` must always round-trip through `OpportunityRadarEngine.SerializePassages`/`DeserializePassages` (camelCase), never a bare `JsonSerializer.Serialize`/`Deserialize<List<RadarPassage>>` call - a bare call emits/expects PascalCase and silently leaves every passage `id`/`text` `undefined` on the frontend (caught during this pivot's browser verification, existing dev-DB rows created before this fix still carry the broken casing).
- Squashed migration: `InitOpportunityRadar` replaces the two checkpoint-era migrations above (deleted, never applied to any database) with one clean initial migration reflecting this final shape.
- `tests/OpportunityRadar.Unit` covers both types' scoring/recommendation rubrics, both dedup paths, passage/preference (de)serialization, and mocked-Jev evaluation for both evaluators.

## Opportunity Radar: review fixes (2026-09-21)

A review of the checkpoint above found several correctness gaps, all fixed here. See `OPPORTUNITY-RADAR.md` for the resulting operator-facing behavior; this is the "what changed and why."

- **Reimport no longer leaves a stale evaluation marked Ready.** `OpportunityImportService`'s update path now checks the matched record's latest evaluation and, if it was `Ready`, appends a new `Stale` evaluation before saving, via a shared `AddStaleEvaluationIfReady` helper. Mirrors the existing capability-change staleness pattern in `UpdatePreferences`.
- **Reimport matching gained a priority order and closed real gaps.** Both `ImportActiveProjectAsync` and `ImportBusinessProspectAsync` now try an exact `ExternalId` match first (same `EntityType` and `IsSynthetic`) before falling back to the previous fingerprint/domain match; `ExternalId` was previously stored but never read. `BusinessProspect` also gained an exact-`NormalizedBusinessName` fallback for domain-less prospects, which is what makes `OPPORTUNITY-RADAR.md`'s "business name" matching claim actually true. All match queries now also require `IsSynthetic` to match, closing a gap where a real import could coincidentally collide with a synthetic sample row. On any update, `ActiveProject` now keeps `Title`/`Fingerprint` and `BusinessProspect` now keeps `Title` in sync with the incoming request (previously the displayed title could silently go stale relative to the record's internal matching key).
- **The digest only shows unreviewed records now.** `Digest` added a `UserDecision is null` filter alongside its existing Recommendation/EvaluationStatus checks, so a record you've already decided on stops reappearing regardless of what the model still recommends.
- **Scoring weights are normalized before use.** `ReadActiveProjectPreferences`/`ReadBusinessProspectPreferences` scale whatever weights are stored so they always sum to 100 before `ComposeActiveProject`/`ComposeBusinessProspect` see them, so the hardcoded 170/250 priority thresholds stay meaningful no matter what an admin enters (previously all-1 weights collapsed nearly everything to Low). `OpportunityRadarEngine.ActiveProjectPreferencesEqual`/`BusinessProspectPreferencesEqual` are new public structural-equality helpers (record equality on these types falls back to array reference equality, which is a real trap - see the divergent-array test cases in `tests/OpportunityRadar.Unit/Program.cs`).
- **Saving preferences no longer resimulates every record unconditionally.** `UpdatePreferences` now compares old vs. new preferences per entity type (via the equality helpers above) and only calls `AddSimulation` for a Simulated-provider evaluation when that entity type's preferences actually changed - a digest-count-only save adds zero new evaluation rows.
- **The list endpoint no longer loads every opportunity and its full evaluation history into memory.** `List` now projects each opportunity's latest evaluation via a correlated subquery and pushes entity type, recommendation, source type, decision, free-text search (`EF.Functions.ILike`), ordering, and pagination into SQL. Only the current page (25 rows) plus a count is ever materialized.
- **Delete and clear-duplicate-flag endpoints exist now.** `DELETE /{id}` removes a record (bad import, synthetic example); cascade to its detail table and evaluation history is DB-enforced (`OnDelete: Cascade`), and any other record's `DuplicateOfId` pointing at it is DB-enforced to `SetNull`, so no manual orphan cleanup is needed. `PATCH /{id}/duplicate` clears a false "possible duplicate" flag. In-place editing of imported fields was deliberately left out of scope - delete and reimport instead.
- New composite index `IX_Opportunities_EntityType_ExternalId` (filtered to non-null `ExternalId`) backs the new lookup; migration `AddOpportunityExternalIdIndex` layers on top of `InitOpportunityRadar` rather than editing it. This migration's `.Designer.cs`/snapshot were hand-authored (not generated via `dotnet ef migrations add`) because another local session's `dotnet run` held the standard build output locked at the time - the change is a single filtered index addition mirrored exactly from the existing migration's structure, but verify it applies cleanly with `dotnet ef database update` before trusting it further.
- `tests/OpportunityRadar.Unit` gained weight-normalization scale-invariance cases (proportionally identical but differently-scaled weights must rank identically; all-weights-at-1 must not collapse a strong record to Low; all-zero weights must not divide by zero) and the preferences-equality divergent-array-instance cases described above. The reimport matching/staleness, digest filter, `List` SQL rewrite, and delete/cascade behavior are DB/controller-level and are verified manually against a local Postgres database and the browser instead, consistent with how this feature has been verified throughout - that manual pass is still outstanding as of this note (see "Project Status").

## Opportunity Radar Jev evaluation v2 (2026-09-23)

Jev is the only user-facing evaluator. `OpportunityRadarV2` converts typed Jev judgments into a 0 to 100 score, separate Jev confidence, priority, recommendation, and deterministic Info, Review, or Block checks. Business Prospect classification precedence is human override, Jev, imported type, then Unknown. Imported sourcing-agent type and research confidence are stored for agreement and verification, but are excluded from Jev request state.

Scoring uses three editable relative profiles: Active Project, Operational Pain, and Digital Presence. Hybrid uses Operational Pain. Each profile is normalized to 100, all-zero input falls back to versioned defaults, and the effective normalized profile is stored with the evaluation. Weight and local screening changes append immutable `LocalRecompose` history without provider usage. Active Project capability changes append Stale history because capabilities affect the Jev request.

The old keyword comparison is removed from the API and UI. Detail views show agreement, checks, effective weights, rubric version, and evaluation origin. Legacy Ready evaluations become Stale in `AddOpportunityRadarJevV2`. Do not apply that migration outside a disposable local database or production without Jonathan's explicit approval.
