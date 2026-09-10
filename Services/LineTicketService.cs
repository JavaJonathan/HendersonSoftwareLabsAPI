using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HendersonSoftwareLabsAPI.Services;

public interface ILineTicketService
{
    string Mint(DateTime issuedAtUtc);

    /// <summary>
    /// Seconds since the ticket was issued, or null if it is missing, malformed, tampered with,
    /// future-dated, or older than a day. Callers treat null as zero elapsed, which yields the
    /// tightest possible clear bound rather than an error.
    /// </summary>
    double? ElapsedSeconds(string? ticket, DateTime nowUtc);
}

/// <summary>
/// Stateless proof of when a Line snapshot was issued.
///
/// The clear-tasks endpoint needs to know how long a visitor has been accumulating in order to
/// bound what they can contribute, and a client-supplied elapsed time is worth nothing. A signed
/// ticket gives us that with no cookie (which would drag a consent banner question onto a
/// marketing page), no server-side per-IP state, and nothing that identifies a visitor.
///
/// It is signed with the existing Jwt:Key, already provisioned in SSM in production and in
/// user-secrets locally, so this introduces no new secret to manage.
/// </summary>
public class LineTicketService : ILineTicketService
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly byte[] _key;

    public LineTicketService(IConfiguration configuration)
    {
        var key = configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Jwt:Key must be set; the Line ticket signer shares it.");
        }
        _key = Encoding.UTF8.GetBytes(key);
    }

    public string Mint(DateTime issuedAtUtc)
    {
        var payload = new DateTimeOffset(DateTime.SpecifyKind(issuedAtUtc, DateTimeKind.Utc))
            .ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        return $"{ToBase64Url(Encoding.UTF8.GetBytes(payload))}.{ToBase64Url(Sign(payload))}";
    }

    public double? ElapsedSeconds(string? ticket, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;

        var parts = ticket.Split('.');
        if (parts.Length != 2) return null;

        string payload;
        byte[] presented;
        try
        {
            payload = Encoding.UTF8.GetString(FromBase64Url(parts[0]));
            presented = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        // Constant-time compare so the signature cannot be probed a byte at a time.
        if (!CryptographicOperations.FixedTimeEquals(presented, Sign(payload))) return null;

        if (!long.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var issuedUnix))
        {
            return null;
        }

        var elapsed = (nowUtc - DateTimeOffset.FromUnixTimeSeconds(issuedUnix).UtcDateTime).TotalSeconds;

        // A future-dated ticket means a clock problem or a forgery attempt; either way it earns
        // nothing. An expired one is simply stale.
        if (elapsed < 0 || elapsed > MaxAge.TotalSeconds) return null;

        return elapsed;
    }

    private byte[] Sign(string payload)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
