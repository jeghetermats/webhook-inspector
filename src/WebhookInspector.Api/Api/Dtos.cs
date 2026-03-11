using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Api;

public sealed record MeDto(Guid Id, string Login);

public sealed record EndpointDto(
    Guid Id,
    string Name,
    string CaptureToken,
    DateTimeOffset CreatedAt,
    int RequestCount,
    long BodyBytes);

public sealed record EndpointNameRequest(string? Name);

public sealed record RequestSummaryDto(
    Guid Id,
    string Method,
    string Path,
    string RawQuery,
    DateTimeOffset ReceivedAt,
    string? ContentType,
    int BodySize,
    string? BodyPreview);

public sealed record RequestPageDto(IReadOnlyList<RequestSummaryDto> Items, string? NextCursor);

public sealed record RequestDetailDto(
    Guid Id,
    Guid EndpointId,
    string Method,
    string Path,
    string RawQuery,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<HeaderEntry> Headers,
    string? ContentType,
    int BodySize,
    string BodyBase64,
    string? SourceIp);
