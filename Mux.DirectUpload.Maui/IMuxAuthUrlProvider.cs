namespace Mux.DirectUpload.Maui;

public interface IMuxAuthUrlProvider
{
    /// <summary>
    /// Returns an authenticated Mux Direct Upload PUT URL.
    /// Implement this by calling your secure backend service (not Mux directly from the app).
    /// </summary>
    /// <param name="authContext">Optional metadata for your backend (creator, external id, metadata).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>PUT URL and optional Mux ids when your backend includes them (e.g. <c>uploadId</c>, <c>assetId</c>, <c>playbackId</c>).</returns>
    Task<MuxAuthUrlResult> GetUploadUrlAsync(
        MuxAuthRequestContext? authContext = null,
        CancellationToken cancellationToken = default);
}

