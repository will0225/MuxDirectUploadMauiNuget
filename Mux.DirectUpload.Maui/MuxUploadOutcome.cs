namespace Mux.DirectUpload.Maui;

/// <summary>
/// Result when an upload completes: auth-time ids plus optional fresh details from
/// <see cref="IMuxUploadDetailsProvider"/> (your backend calling Mux GET upload).
/// </summary>
public sealed record MuxUploadOutcome(
    MuxAuthUrlResult Auth,
    MuxUploadDetails? UploadDetails);
