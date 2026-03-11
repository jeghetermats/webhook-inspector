using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace WebhookInspector.Api.Api;

/// <summary>
/// Opaque keyset cursor on (ReceivedAt, Id), the same order as the list index.
/// Stable while new requests arrive, unlike offset pagination.
/// </summary>
public readonly record struct RequestCursor(DateTimeOffset ReceivedAt, Guid Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes(
            $"{ReceivedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{Id:N}"));

    public static bool TryDecode(string? value, out RequestCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(value) || value.Length > 100)
        {
            return false;
        }

        try
        {
            var parts = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(value)).Split(':');
            if (parts.Length == 2 &&
                long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
                ticks is >= 0 and <= 3155378975999999999 &&
                Guid.TryParseExact(parts[1], "N", out var id))
            {
                cursor = new RequestCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}
