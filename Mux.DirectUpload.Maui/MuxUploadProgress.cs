namespace Mux.DirectUpload.Maui;

public sealed class MuxUploadProgress
{
    public long BytesSent { get; }
    public long? TotalBytes { get; }

    public double? Percent =>
        TotalBytes.HasValue && TotalBytes.Value > 0
            ? (double)BytesSent / TotalBytes.Value * 100.0
            : null;

    public MuxUploadProgress(long bytesSent, long? totalBytes)
    {
        BytesSent = bytesSent;
        TotalBytes = totalBytes;
    }
}

