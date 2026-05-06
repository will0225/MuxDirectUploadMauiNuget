namespace Mux.DirectUpload.Maui;

/// <summary>
/// Serializable state for resuming a Mux direct upload after app restart or crash.
/// Persist this (e.g. JSON file, SQLite) between launches.
/// When using the file-path continuation API, keep <see cref="LocalFilePath"/> pointing at readable bytes until the upload completes.
/// When <see cref="LocalFilePath"/> is empty, resume using <see cref="MuxDirectUploader"/>'s persisted continuation overload that accepts a seekable stream factory.
/// </summary>
public sealed class MuxResumableUploadSession
{
    /// <summary>Stable id for your storage row.</summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Mux signed PUT URL (<see cref="MuxAuthUrlResult.PutUri"/> absolute string).</summary>
    public string PutUri { get; set; } = "";

    public string? UploadId { get; set; }

    public string? AssetId { get; set; }

    public string? PlaybackId { get; set; }

    /// <summary>
    /// Local video file to read bytes from when using the file-path persisted continuation overload.
    /// May be empty when the session was created from a stream-only API and you resume with a stream factory.
    /// </summary>
    public string LocalFilePath { get; set; } = "";

    public long FileSizeBytes { get; set; }

    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;

    public string? ContentType { get; set; }

    /// <summary>Best-effort byte offset after the last fully acknowledged chunk (also updated after server probe).</summary>
    public long BytesUploadedSoFar { get; set; }

    public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public int SchemaVersion { get; set; } = 1;
}
