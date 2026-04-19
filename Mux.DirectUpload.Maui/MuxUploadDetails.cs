using System.Text.Json.Serialization;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Direct Upload object from <c>GET /video/v1/uploads/{id}</c>, optionally merged by your backend with
/// asset fields (playback ids) from <c>GET /video/v1/assets/{id}</c>.
/// </summary>
/// <remarks>
/// Playback IDs are not on the upload resource; they come from the Asset API. The sample Firebase function
/// merges <see cref="PlaybackId"/> and <see cref="PlaybackIds"/> after fetching the asset when <c>asset_id</c> is set.
/// Creator / external / custom meta are echoed under <see cref="NewAssetSettings"/> (Mux copies your create-upload input).
/// </remarks>
public sealed class MuxUploadDetails
{
    public string? Id { get; init; }

    public string? Status { get; init; }

    [JsonPropertyName("asset_id")]
    public string? AssetId { get; init; }

    /// <summary>Echo of what you passed at create time: <c>passthrough</c>, <c>meta</c>, etc.</summary>
    [JsonPropertyName("new_asset_settings")]
    public MuxUploadNewAssetSettings? NewAssetSettings { get; init; }

    [JsonPropertyName("error")]
    public MuxUploadError? Error { get; init; }

    /// <summary>First playback id (sample Firebase merges from asset GET).</summary>
    [JsonPropertyName("playbackId")]
    public string? PlaybackId { get; init; }

    [JsonPropertyName("playback_ids")]
    public List<MuxPlaybackIdItem>? PlaybackIds { get; init; }

    /// <summary>From asset when backend enriches (e.g. <c>preparing</c>, <c>ready</c>).</summary>
    [JsonPropertyName("asset_status")]
    public string? AssetStatus { get; init; }

    /// <summary>Asset <c>meta</c> after ingest (sample Firebase).</summary>
    [JsonPropertyName("asset_meta")]
    public Dictionary<string, string>? AssetMeta { get; init; }

    /// <summary>Present when details come from a webhook/Firestore snapshot (e.g. sample <c>getMuxWebhookStatus</c>), not from Mux GET upload.</summary>
    [JsonPropertyName("lastEventType")]
    public string? LastEventType { get; init; }
}

/// <summary><c>new_asset_settings</c> echoed on the upload object (includes your meta / passthrough).</summary>
public sealed class MuxUploadNewAssetSettings
{
    public string? Passthrough { get; init; }

    [JsonPropertyName("meta")]
    public Dictionary<string, string>? Meta { get; init; }
}

public sealed class MuxPlaybackIdItem
{
    public string? Id { get; init; }

    public string? Policy { get; init; }
}

public sealed class MuxUploadError
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
