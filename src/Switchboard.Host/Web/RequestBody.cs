using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Switchboard.Application.Options;

namespace Switchboard.Host.Web;

/// <summary>Endpoint metadata: a request body limit of its own, below the global one.</summary>
internal sealed record BodyLimitMetadata(long MaxBytes);

/// <summary>
/// What the one body reader found. <see cref="TooLarge"/> is a body past the limit (never parsed);
/// otherwise the bytes are whatever the client sent, and <see cref="AsObject"/> or
/// <see cref="As{T}"/> say whether they are the JSON the endpoint expects (null when they are not).
/// </summary>
internal readonly record struct BodyRead(ReadOnlyMemory<byte> Bytes, bool TooLarge, long Limit)
{
    /// <summary>The body as one JSON object, nesting of at most four levels; null when it is anything else, empty included.</summary>
    public JsonElement? AsObject()
    {
        if (TooLarge || Bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Bytes, new JsonDocumentOptions { MaxDepth = 4 });
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The body deserialized with <paramref name="options"/>; null when it is not that JSON (or is the JSON <c>null</c>).</summary>
    public T? As<T>(JsonSerializerOptions options)
        where T : class
    {
        if (TooLarge)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(Bytes.Span, options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class RequestBody
{
    /// <summary>
    /// The single place a request body is read. The limit is the global one
    /// (<see cref="IngressOptions.MaxRequestBodyBytes"/>) or, where an endpoint holds a smaller one
    /// (<see cref="WithBodyLimit"/>), that. A declared length above it is refused unread; a body without
    /// one (chunked) is cut off as it grows past it, whether by this count or by the server's own limit.
    /// </summary>
    public static async Task<BodyRead> ReadBodyAsync(this HttpRequest request, CancellationToken cancellationToken)
    {
        var limit = request.HttpContext.RequestServices.GetRequiredService<IOptions<IngressOptions>>().Value.MaxRequestBodyBytes;
        if (request.HttpContext.GetEndpoint()?.Metadata.GetMetadata<BodyLimitMetadata>() is { } own)
        {
            limit = Math.Min(limit, own.MaxBytes);
        }

        if (request.ContentLength > limit)
        {
            return new BodyRead(default, true, limit);
        }

        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.GetMemory(4096), cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Advance(read);
                if (buffer.WrittenCount > limit)
                {
                    return new BodyRead(default, true, limit);
                }
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new BodyRead(default, true, limit);
        }

        return new BodyRead(buffer.WrittenMemory, false, limit);
    }

    /// <summary>Gives the endpoint a body limit of its own; it can only lower the global one.</summary>
    public static TBuilder WithBodyLimit<TBuilder>(this TBuilder builder, long maxBytes)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new BodyLimitMetadata(maxBytes));
}
