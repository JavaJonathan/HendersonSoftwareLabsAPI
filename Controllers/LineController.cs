using System.Text.Json;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Models;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace HendersonSoftwareLabsAPI.Controllers;

/// <summary>
/// The shared "Line" on the marketing homepage: six kinds of work, some automated and some still
/// done by hand. Visitors clear the manual ones by clicking, and those clears accumulate across
/// everyone until a kind crosses a threshold and becomes automated permanently, for everybody.
///
/// This is the only anonymous write surface in the API, so a few things are deliberate:
/// <list type="bullet">
/// <item>[AllowAnonymous] is required at class level. Program.cs sets a fail-closed
/// FallbackPolicy (RequireAuthenticatedUser), so without it every action here returns 401 with no
/// obvious cause.</item>
/// <item>The only caller-supplied content is integers keyed by a fixed enum name. There is no free
/// text anywhere in the request, which is what keeps this feature free of a moderation story.</item>
/// <item>Writes are bounded three ways: an HMAC ticket proves how long the caller has been
/// accumulating, the per-kind clamp comes from the game's own arrival rate, and a per-IP daily
/// budget silently stops counting past a sane total. The rate limiter is only a backstop.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/line")]
[AllowAnonymous]
public class LineController : ControllerBase
{
    private const string SummaryCacheKey = "line:summary";
    private static readonly TimeSpan SummaryCacheFor = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions BodyJson = new() { PropertyNameCaseInsensitive = true };

    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILineTicketService _tickets;

    public LineController(ApplicationDbContext db, IMemoryCache cache, ILineTicketService tickets)
    {
        _db = db;
        _cache = cache;
        _tickets = tickets;
    }

    /// <summary>
    /// The whole shared Line in one payload. The client polls this every few seconds while the
    /// section is on screen, so it is cached briefly: a burst of visitors costs one query, not one
    /// query each.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<LineSummaryModel>> GetLine()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await GetSummaryAsync());
    }

    [HttpPost("clears")]
    [EnableRateLimiting("line-clears")]
    public async Task<ActionResult<ClearTasksResponseModel>> ClearTasks()
    {
        Response.Headers.CacheControl = "no-store";

        // Read and deserialize the body by hand rather than taking a [FromBody] parameter. A
        // cross-origin sendBeacon cannot perform a CORS preflight, so it has to be CORS-simple,
        // which means a text/plain content type that the [FromBody] binder refuses. One code path
        // here serves both the normal fetch and the page-hide beacon.
        ClearTasksRequestModel? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ClearTasksRequestModel>(
                Request.Body, BodyJson, HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return BadRequest(new { message = "Could not read that request." });
        }

        if (request is null)
        {
            return BadRequest(new { message = "Could not read that request." });
        }

        var now = DateTime.UtcNow;

        // A missing, forged, or stale ticket is not an error, it just earns the tightest possible
        // bound. Stripping the ticket is the worst move available to a cheater, not a bypass.
        var elapsed = _tickets.ElapsedSeconds(request.Ticket, now) ?? 0;
        var perKindBound = LineGameRules.ClearBound(elapsed);

        var requested = ParseClears(request.Clears);
        var firstTime = ParseKinds(request.FirstTime);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var budgetKey = $"line:budget:{ip}";
        var budgetUsed = _cache.TryGetValue<int>(budgetKey, out var used) ? used : 0;

        var allowance = Math.Min(
            LineGameRules.MaxTotalPerRequest,
            Math.Max(0, LineGameRules.DailyIpBudget - budgetUsed));

        var accepted = new Dictionary<LineKind, int>();
        foreach (var (kind, count) in requested)
        {
            if (allowance <= 0) break;
            var take = Math.Clamp(count, 0, Math.Min(perKindBound, allowance));
            if (take <= 0) continue;
            accepted[kind] = take;
            allowance -= take;
        }

        var acceptedTotal = accepted.Values.Sum();
        if (acceptedTotal > 0)
        {
            _cache.Set(budgetKey, budgetUsed + acceptedTotal, BudgetWindow);
        }

        var summary = acceptedTotal > 0
            ? await ApplyClearsAsync(accepted, firstTime, now)
            : await GetSummaryAsync();

        // Over budget is answered with a normal 200 and a zero credit. The visitor's pile already
        // cleared in their browser, so punishing them with an error would only break a homepage.
        return Ok(new ClearTasksResponseModel
        {
            SnapshotAt = summary.SnapshotAt,
            TotalHandCleared = summary.TotalHandCleared,
            UnlockedCount = summary.UnlockedCount,
            NextThreshold = summary.NextThreshold,
            Ticket = summary.Ticket,
            Kinds = summary.Kinds,
            Accepted = accepted.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value)
        });
    }

    /// <summary>
    /// Credits the clears and decides an unlock in one atomic step.
    ///
    /// All six rows are locked in primary key order, so concurrent flushes serialise rather than
    /// deadlock, and the unlock decision sees a settled count of how many kinds are already
    /// automated. That is what makes a double unlock impossible: the threshold is a function of
    /// that count, and only one transaction holds the locks at a time.
    /// </summary>
    private async Task<LineSummaryModel> ApplyClearsAsync(
        Dictionary<LineKind, int> accepted,
        HashSet<LineKind> firstTime,
        DateTime now)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var rows = await _db.LineKindProgress
            .FromSqlRaw("""SELECT * FROM "LineKindProgress" ORDER BY "Kind" FOR UPDATE""")
            .ToListAsync();

        foreach (var (kind, count) in accepted)
        {
            var row = rows.FirstOrDefault(r => r.Kind == kind);
            if (row is null) continue;

            row.HandCleared += count;
            if (firstTime.Contains(kind)) row.Helpers += 1;
        }

        var threshold = LineGameRules.UnlockThresholdFor(rows.Count(r => r.UnlockedAt is not null));
        if (threshold is not null)
        {
            var winner = rows
                .Where(r => r.UnlockedAt is null && r.Kind != LineGameRules.ReservedManualKind)
                .Where(r => r.HandCleared >= threshold.Value)
                .OrderByDescending(r => r.HandCleared)
                .ThenBy(r => r.Kind)
                .FirstOrDefault();

            if (winner is not null) winner.UnlockedAt = now;
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        // Set the cache rather than only evicting it, so a write warms the next read instead of
        // making the next visitor pay for a query.
        var summary = BuildSummary(rows, DateTime.UtcNow);
        _cache.Set(SummaryCacheKey, summary, SummaryCacheFor);
        return summary;
    }

    private async Task<LineSummaryModel> GetSummaryAsync()
    {
        if (_cache.TryGetValue(SummaryCacheKey, out LineSummaryModel? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _db.LineKindProgress.AsNoTracking().ToListAsync();
        var summary = BuildSummary(rows, DateTime.UtcNow);
        _cache.Set(SummaryCacheKey, summary, SummaryCacheFor);
        return summary;
    }

    private LineSummaryModel BuildSummary(List<LineKindProgress> rows, DateTime now)
    {
        var unlocked = rows.Count(r => r.UnlockedAt is not null);

        return new LineSummaryModel
        {
            SnapshotAt = now,
            TotalHandCleared = rows.Sum(r => r.HandCleared),
            UnlockedCount = unlocked,
            NextThreshold = LineGameRules.UnlockThresholdFor(unlocked),
            // Minted here and shared by everyone served from this cache entry. Two seconds of
            // shared issue time loosens the clear bound by 0.4 of a task, which is nothing.
            Ticket = _tickets.Mint(now),
            Kinds = rows.ToDictionary(
                r => r.Kind.ToString(),
                r => new LineKindModel
                {
                    HandCleared = r.HandCleared,
                    Helpers = r.Helpers,
                    UnlockedAt = r.UnlockedAt
                })
        };
    }

    /// <summary>Unknown kind names and non-positive counts are dropped rather than rejected.</summary>
    private static Dictionary<LineKind, int> ParseClears(Dictionary<string, int>? clears)
    {
        var parsed = new Dictionary<LineKind, int>();
        if (clears is null) return parsed;

        foreach (var (name, count) in clears)
        {
            if (count <= 0) continue;
            if (TryParseKind(name, out var kind)) parsed[kind] = count;
        }

        return parsed;
    }

    private static HashSet<LineKind> ParseKinds(List<string>? names)
    {
        var parsed = new HashSet<LineKind>();
        if (names is null) return parsed;

        foreach (var name in names)
        {
            if (TryParseKind(name, out var kind)) parsed.Add(kind);
        }

        return parsed;
    }

    /// <summary>
    /// Matches against the enum's own names rather than Enum.TryParse, which would also accept "3"
    /// or "Intake, Notify" and quietly credit something the caller never named.
    /// </summary>
    private static bool TryParseKind(string? name, out LineKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var match = Enum.GetNames<LineKind>()
            .FirstOrDefault(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));

        if (match is null) return false;

        kind = Enum.Parse<LineKind>(match);
        return true;
    }
}
