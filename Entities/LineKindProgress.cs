namespace HendersonSoftwareLabsAPI.Entities;

/// <summary>
/// The shared state of one kind of work on the homepage Line. Six rows, seeded by the migration,
/// forever. There is no insert path and no delete path, only updates to these six.
///
/// The governing rule is <b>time is never stored, only events are</b>. Nothing here is a figure
/// that has to be ticked on a schedule: how much work an automation has got through is derived on
/// the client from <see cref="UnlockedAt"/> and the current clock, and the size of a visitor's
/// backlog is derived in their own browser from when they last cleared it. So there is no counter
/// that decays if the site goes quiet, nothing that is wrong if a write is lost, and no write
/// traffic at all beyond the batched flushes below.
///
/// There is deliberately no owner column and no IP: the endpoint is anonymous, a visitor's own
/// contribution is remembered in their localStorage, and storing anything that identifies a
/// visitor would turn a toy into a privacy question.
/// </summary>
public class LineKindProgress
{
    /// <summary>Natural primary key. The row set is fixed, so there is no identity sequence to burn.</summary>
    public LineKind Kind { get; set; }

    /// <summary>
    /// Every task of this kind that visitors have cleared by hand, ever. Monotone. This is what
    /// drives the unlock ladder, and it is the only number strangers can move.
    /// </summary>
    public long HandCleared { get; set; }

    /// <summary>How many distinct visitors have cleared at least one task of this kind. A count, never a list.</summary>
    public int Helpers { get; set; }

    /// <summary>
    /// When this kind became automated, or null while it is still done by hand. The only shared
    /// value that changes what anyone else sees on the belt.
    /// </summary>
    public DateTime? UnlockedAt { get; set; }
}
