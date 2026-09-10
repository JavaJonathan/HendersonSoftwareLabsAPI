using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

/// <summary>
/// The shared rules of the homepage Line. These constants are mirrored in the client's
/// lineModel.ts and a drift between the two is silent (it shows up only as clears being
/// mysteriously rejected), so the UI repo carries a unit test that asserts
/// <see cref="ClearBound"/> against the same table of elapsed values used here.
/// </summary>
public static class LineGameRules
{
    /// <summary>Tasks of one kind that arrive per second. One every five seconds.</summary>
    public const double ArrivalPerSec = 0.2;

    /// <summary>The most tasks of one kind that can ever be waiting. An emptied kind refills in 30 seconds.</summary>
    public const int CapPerKind = 6;

    /// <summary>Never automated, however much work goes through it. Some calls are judgement, not rules.</summary>
    public const LineKind ReservedManualKind = LineKind.Report;

    /// <summary>Hand-cleared totals that unlock the next automation, in order.</summary>
    public static readonly int[] UnlockThresholds = [250, 750, 2000, 5000];

    /// <summary>A single request can never contribute more than this to one kind, or this in total.</summary>
    public const int MaxPerKindPerRequest = 400;
    public const int MaxTotalPerRequest = 800;

    /// <summary>Accepted clears one IP can contribute in a rolling day before its writes stop counting.</summary>
    public const int DailyIpBudget = 3000;

    /// <summary>
    /// The target for the next unlock, or null once the ladder is spent. Indexed by how many
    /// kinds are already automated; the seeded <see cref="LineKind.Intake"/> occupies index 0, so
    /// the first threshold a visitor can actually reach is UnlockThresholds[0].
    /// </summary>
    public static int? UnlockThresholdFor(int unlockedCount)
    {
        var index = unlockedCount - 1;
        if (index < 0) index = 0;
        return index < UnlockThresholds.Length ? UnlockThresholds[index] : null;
    }

    /// <summary>
    /// The most tasks of one kind that could plausibly have been cleared in <paramref name="elapsedSeconds"/>:
    /// whatever was already waiting, plus whatever arrived since. The bound comes from the game's
    /// own physics rather than a tuned guess, so it never needs revisiting.
    ///
    /// Reference values, mirrored by the UI's serverClearBound test:
    ///   elapsed    0s ->   6      elapsed  300s ->  66
    ///   elapsed    5s ->   7      elapsed  900s -> 186
    ///   elapsed   30s ->  12      elapsed 5000s -> 186 (clamped at 900s)
    /// </summary>
    public static int ClearBound(double elapsedSeconds)
    {
        var elapsed = Math.Clamp(elapsedSeconds, 0, 900);
        var bound = CapPerKind + (int)Math.Ceiling(ArrivalPerSec * elapsed);
        return Math.Min(bound, MaxPerKindPerRequest);
    }
}
