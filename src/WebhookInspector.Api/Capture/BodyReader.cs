using System.Buffers;
using Microsoft.AspNetCore.Http.Features;

namespace WebhookInspector.Api.Capture;

public static class BodyReader
{
    /// <summary>
    /// Reads the whole request body into memory, or returns null as soon as it is known
    /// to exceed <paramref name="limit"/>. Runs before any database work, so a slow
    /// upload never holds a transaction or a row lock.
    /// </summary>
    public static async Task<byte[]?> ReadLimitedAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        // Also let Kestrel enforce the limit for chunked bodies without a Content-Length.
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }

        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }
}
