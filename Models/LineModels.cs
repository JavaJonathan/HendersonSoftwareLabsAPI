namespace HendersonSoftwareLabsAPI.Models;

/// <summary>The shared state of one kind of work. Everything else the client shows is derived from these.</summary>
public class LineKindModel
{
    public long HandCleared { get; set; }
    public int Helpers { get; set; }
    public DateTime? UnlockedAt { get; set; }
}

/// <summary>
/// One snapshot of the shared Line, returned by both the read and the write endpoint so a client
/// never has to make a second call to find out where things stand.
/// </summary>
public class LineSummaryModel
{
    /// <summary>Server clock when this was built. The client refuses any snapshot older than the one it has applied.</summary>
    public DateTime SnapshotAt { get; set; }

    public long TotalHandCleared { get; set; }
    public int UnlockedCount { get; set; }

    /// <summary>
    /// Hand-cleared total that unlocks the next automation, or null once every unlockable kind is
    /// automated. Sent by the server rather than recomputed on the client so the two can never
    /// disagree about what the goal is.
    /// </summary>
    public int? NextThreshold { get; set; }

    /// <summary>
    /// Signed, stateless proof of when this snapshot was issued. The client sends it back with its
    /// clears so the server can bound them without trusting a client-supplied elapsed time. It
    /// travels in the body rather than a header so a cross-origin sendBeacon stays CORS-simple.
    /// </summary>
    public string? Ticket { get; set; }

    public Dictionary<string, LineKindModel> Kinds { get; set; } = [];
}

public class ClearTasksRequestModel
{
    public string? Ticket { get; set; }

    /// <summary>Clears keyed by kind name. Unknown names are ignored rather than rejected.</summary>
    public Dictionary<string, int>? Clears { get; set; }

    /// <summary>Kinds this visitor is contributing to for the first time, so Helpers counts people rather than flushes.</summary>
    public List<string>? FirstTime { get; set; }
}

/// <summary>
/// The post-write snapshot, plus exactly what was credited. The client reconciles against
/// <see cref="Accepted"/> rather than against what it sent, so a clamped or budgeted request
/// settles cleanly instead of leaving a permanent optimistic overhang.
/// </summary>
public class ClearTasksResponseModel : LineSummaryModel
{
    public Dictionary<string, int> Accepted { get; set; } = [];
}
