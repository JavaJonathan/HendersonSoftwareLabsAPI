namespace HendersonSoftwareLabsAPI.Entities;

/// <summary>
/// The six kinds of work that flow through the homepage's shared Line.
///
/// This enum is the entire anti-abuse story for that feature. The clear-tasks endpoint is
/// anonymous, and because the only caller-supplied content is a count against one of these fixed
/// names, there is no free text to moderate, no profanity to filter, no link to spam, and no
/// injection surface. The request body is integers keyed by these names and nothing else. Keep it
/// that way: visitor-supplied labels would be a different feature with a moderation story
/// attached, not a widening of this enum.
/// </summary>
public enum LineKind
{
    Intake,
    Validate,
    Invoice,
    Notify,
    Sync,
    Report
}
