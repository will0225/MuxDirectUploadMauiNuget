namespace Mux.DirectUpload.Maui;

/// <summary>
/// Result of your auth-url call: the Mux storage PUT URL plus optional ids returned by your backend
/// (typically echoed from the Mux Direct Upload create response). The same value is returned when the upload task completes successfully.
/// </summary>
/// <param name="PutUri">Signed URL for the HTTP PUT to Mux storage.</param>
/// <param name="UploadId">Mux Direct Upload id when your JSON includes <c>uploadId</c>.</param>
/// <param name="AssetId">Mux Asset id when your JSON includes <c>assetId</c> (often null until ingest completes).</param>
/// <param name="PlaybackId">A Mux playback id when your JSON includes <c>playbackId</c> (often null until the asset is ready).</param>
public sealed record MuxAuthUrlResult(
    Uri PutUri,
    string? UploadId,
    string? AssetId,
    string? PlaybackId);
