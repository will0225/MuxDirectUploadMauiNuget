using System.Text.Json.Serialization;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// JSON shape returned by the sample Firebase function <c>GET getMuxWebhookStatus</c> (Firestore document).
/// </summary>
public sealed class MuxWebhookStatusSnapshot
{
    public string? UpdatedAt { get; init; }

    public string? LastEventType { get; init; }

    public string? UploadId { get; init; }

    public string? AssetId { get; init; }

    public string? PlaybackId { get; init; }

    public List<MuxPlaybackIdItem>? PlaybackIds { get; init; }

    [JsonPropertyName("assetStatus")]
    public string? AssetStatus { get; init; }

    public string? Passthrough { get; init; }
}
