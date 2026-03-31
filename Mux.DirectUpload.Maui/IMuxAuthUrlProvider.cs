namespace Mux.DirectUpload.Maui;

public interface IMuxAuthUrlProvider
{
    /// <summary>
    /// Returns an authenticated Mux Direct Upload PUT URL.
    /// Implement this by calling your secure backend service (not Mux directly from the app).
    /// </summary>
    Task<Uri> GetUploadUrlAsync(CancellationToken cancellationToken = default);
}

