using System.Text;

namespace WebhookInspector.Api.Api;

public static class BodyPreview
{
    /// <summary>Bytes fetched from the database for the list preview.</summary>
    public const int PrefixBytes = 160;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// A short text preview, or null when the body is empty or does not look like text.
    /// The prefix may end in the middle of a multi-byte character, which is trimmed.
    /// </summary>
    public static string? FromPrefix(byte[] prefix)
    {
        if (prefix.Length == 0)
        {
            return null;
        }

        for (var cut = 0; cut <= 3 && cut < prefix.Length; cut++)
        {
            try
            {
                var text = StrictUtf8.GetString(prefix, 0, prefix.Length - cut);
                return text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ? null : text;
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return null;
    }
}
