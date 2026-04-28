namespace Mux.DirectUpload.Maui;

public sealed class MuxUploadHandle
{
    private readonly CancellationTokenSource _cts;
    private readonly MuxUploadPauseController? _pauseController;

    internal MuxUploadHandle(CancellationTokenSource cts)
        : this(cts, pauseController: null)
    {
    }

    internal MuxUploadHandle(CancellationTokenSource cts, MuxUploadPauseController? pauseController)
    {
        _cts = cts;
        _pauseController = pauseController;
    }

    public bool CanPause => _pauseController is not null;

    public bool IsPaused => _pauseController?.IsPaused == true;

    /// <summary>Pauses a resumable/chunked upload after the current in-flight chunk finishes.</summary>
    public void Pause() => _pauseController?.Pause();

    /// <summary>Resumes a resumable/chunked upload that was paused with <see cref="Pause"/>.</summary>
    public void Resume() => _pauseController?.Resume();

    public void Cancel() => _cts.Cancel();
}

internal sealed class MuxUploadPauseController
{
    private readonly object _gate = new();
    private TaskCompletionSource? _resumeSignal;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _resumeSignal is not null;
        }
    }

    public void Pause()
    {
        lock (_gate)
            _resumeSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? signal;
        lock (_gate)
        {
            signal = _resumeSignal;
            _resumeSignal = null;
        }

        signal?.TrySetResult();
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? waitTask;
            lock (_gate)
                waitTask = _resumeSignal?.Task;

            if (waitTask is null)
                return;

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

