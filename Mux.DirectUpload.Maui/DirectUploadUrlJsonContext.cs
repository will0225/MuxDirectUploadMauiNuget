using System.Text.Json.Serialization;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Auth backends typically return camelCase JSON: <c>{"uploadUrl":"..."}</c> and optional ids.
/// </summary>
internal sealed class DirectUploadUrlResponse
{
    public required string UploadUrl { get; init; }

    public string? UploadId { get; init; }

    public string? AssetId { get; init; }

    public string? PlaybackId { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DirectUploadUrlResponse))]
internal sealed partial class DirectUploadUrlJsonContext : JsonSerializerContext { }

internal static class DirectUploadUrlResponseMapper
{
    public static MuxAuthUrlResult ToMuxAuthUrlResult(DirectUploadUrlResponse r)
    {
        if (string.IsNullOrWhiteSpace(r.UploadUrl))
            throw new InvalidOperationException("Auth response did not contain uploadUrl.");

        return new MuxAuthUrlResult(
            new Uri(r.UploadUrl, UriKind.Absolute),
            NullIfWhiteSpace(r.UploadId),
            NullIfWhiteSpace(r.AssetId),
            NullIfWhiteSpace(r.PlaybackId));
    }

    private static string? NullIfWhiteSpace(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

