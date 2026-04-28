using System.Text.Json.Serialization;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Auth backends typically return camelCase JSON: <c>{"uploadUrl":"..."}</c> and optional ids.
/// Raw Mux responses (<c>{"data":{"url":"...","id":"..."}}</c>) are also accepted.
/// </summary>
internal sealed class DirectUploadUrlResponse
{
    public string? UploadUrl { get; init; }

    public string? UploadId { get; init; }

    public string? AssetId { get; init; }

    public string? PlaybackId { get; init; }

    public DirectUploadMuxData? Data { get; init; }
}

internal sealed class DirectUploadMuxData
{
    public string? Url { get; init; }

    public string? Id { get; init; }

    [JsonPropertyName("asset_id")]
    public string? AssetId { get; init; }

    [JsonPropertyName("playbackId")]
    public string? PlaybackId { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DirectUploadUrlResponse))]
[JsonSerializable(typeof(DirectUploadMuxData))]
internal sealed partial class DirectUploadUrlJsonContext : JsonSerializerContext { }

internal static class DirectUploadUrlResponseMapper
{
    public static MuxAuthUrlResult ToMuxAuthUrlResult(DirectUploadUrlResponse r)
    {
        var uploadUrl = FirstNonWhiteSpace(r.UploadUrl, r.Data?.Url);
        if (string.IsNullOrWhiteSpace(uploadUrl))
            throw new InvalidOperationException("Auth response did not contain uploadUrl.");

        return new MuxAuthUrlResult(
            new Uri(uploadUrl, UriKind.Absolute),
            FirstNonWhiteSpace(r.UploadId, r.Data?.Id),
            FirstNonWhiteSpace(r.AssetId, r.Data?.AssetId),
            FirstNonWhiteSpace(r.PlaybackId, r.Data?.PlaybackId));
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}

