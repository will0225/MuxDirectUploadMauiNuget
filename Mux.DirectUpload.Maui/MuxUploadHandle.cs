namespace Mux.DirectUpload.Maui;

public sealed class MuxUploadHandle
{
    private readonly CancellationTokenSource _cts;

    internal MuxUploadHandle(CancellationTokenSource cts)
    {
        _cts = cts;
    }

    public void Cancel() => _cts.Cancel();
}

