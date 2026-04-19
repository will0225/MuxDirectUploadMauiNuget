namespace Mux.DirectUpload.Maui;

/// <summary>
/// Fetches Mux Direct Upload details after the PUT completes. Implement this by calling your backend,
/// which should use Mux credentials to call <c>GET https://api.mux.com/video/v1/uploads/{upload_id}</c>.
/// Do not put Mux API tokens in the mobile app.
/// </summary>
public interface IMuxUploadDetailsProvider
{
    /// <summary>
    /// Returns upload status and <see cref="MuxUploadDetails.AssetId"/> when Mux has linked an asset, or null if the request failed.
    /// </summary>
    Task<MuxUploadDetails?> GetUploadDetailsAsync(string uploadId, CancellationToken cancellationToken = default);
}
