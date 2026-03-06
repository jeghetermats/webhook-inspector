using System.Buffers.Text;
using System.Security.Cryptography;

namespace WebhookInspector.Api.Capture;

public static class CaptureToken
{
    private const int ByteLength = 16; // 128 random bits
    public const int Length = 22;      // base64url without padding

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>
    /// Cheap format check so malformed tokens are rejected without a database round trip
    /// and never become rate-limiter partitions.
    /// </summary>
    public static bool IsWellFormed(ReadOnlySpan<char> token)
    {
        if (token.Length != Length)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Extracts the token from a request path of the form /h/{token}[/...].</summary>
    public static bool TryGetFromPath(PathString path, out string token)
    {
        token = string.Empty;
        if (!path.StartsWithSegments("/h", out var rest) || !rest.HasValue)
        {
            return false;
        }

        var value = rest.Value.AsSpan(1); // skip the leading "/"
        var slash = value.IndexOf('/');
        var candidate = slash < 0 ? value : value[..slash];
        if (!IsWellFormed(candidate))
        {
            return false;
        }

        token = candidate.ToString();
        return true;
    }
}
