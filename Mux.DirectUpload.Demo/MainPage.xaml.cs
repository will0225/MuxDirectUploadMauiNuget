using System.Linq;
using System.Text.Json;
#if WINDOWS
using System.Security.Principal;
#endif
using Mux.DirectUpload.Maui;

namespace Mux.DirectUpload.Demo;

public partial class MainPage : ContentPage
{
    private readonly MuxUploadSqliteSessionStore _uploadSessionStore = new();

    private FileResult? _pickedVideo;
    private MuxUploadHandle? _currentUploadHandle;

    public MainPage()
    {
        InitializeComponent();
        ClearResultLabels();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = RefreshResumeSavedUploadUiAsync();
    }

    private async Task RefreshResumeSavedUploadUiAsync()
    {
        var can = await CanResumeSavedUploadAsync().ConfigureAwait(false);
        var s = can ? await TryLoadSessionAsync().ConfigureAwait(false) : null;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ResumeSavedUploadButton.IsEnabled = can;
            if (can && s is not null)
            {
                StatusLabel.Text =
                    $"Status: saved upload session found ({s.BytesUploadedSoFar}/{s.FileSizeBytes} bytes last known) — tap “Resume saved upload” or start a new upload.";
            }
        });
    }

    private Task<MuxResumableUploadSession?> TryLoadSessionAsync(CancellationToken cancellationToken = default) =>
        _uploadSessionStore.TryLoadAsync(cancellationToken);

    private async Task<bool> CanResumeSavedUploadAsync()
    {
        var s = await TryLoadSessionAsync().ConfigureAwait(false);
        return s is not null
               && !string.IsNullOrWhiteSpace(s.LocalFilePath)
               && File.Exists(s.LocalFilePath);
    }

    private Task SaveSessionAsync(MuxResumableUploadSession session, CancellationToken cancellationToken = default) =>
        _uploadSessionStore.SaveAsync(session, cancellationToken);

    private Task ClearSessionAsync(CancellationToken cancellationToken = default) =>
        _uploadSessionStore.ClearAsync(cancellationToken);

    private Task PersistSessionAsync(MuxResumableUploadSession session, CancellationToken cancellationToken) =>
        SaveSessionAsync(session, cancellationToken);

    private sealed record UploadUiSetup(
        MuxDirectUploader Uploader,
        IProgress<MuxUploadProgress> Progress,
        bool FetchDetailsAfterPut,
        bool UseWebhookStatus);

    private UploadUiSetup CreateUploadSetupForPage(HttpClient httpClient, bool looksLikeFirebaseFunction)
    {
        var authEndpointPath = string.IsNullOrWhiteSpace(EndpointPathEntry.Text)
            ? "/muxpackageauthapi/us-central1/getMuxDirectUploadUrl"
            : EndpointPathEntry.Text.Trim();

        var firebaseToken = FirebaseIdTokenEntry.Text?.Trim();
        IMuxAuthUrlProvider authProvider = string.IsNullOrWhiteSpace(firebaseToken)
            ? new HttpMuxAuthUrlProvider(httpClient, authEndpointPath)
            : new BearerMuxAuthUrlProvider(
                httpClient,
                endpointPath: authEndpointPath,
                getBearerTokenAsync: ct => Task.FromResult(firebaseToken ?? string.Empty));

        var fetchDetailsAfterPut = looksLikeFirebaseFunction && !string.IsNullOrWhiteSpace(firebaseToken);
        var useWebhookStatus = UseWebhookStatusSwitch.IsToggled;
        IMuxUploadDetailsProvider? detailsProvider = null;
        if (fetchDetailsAfterPut)
        {
            if (useWebhookStatus)
            {
                detailsProvider = new HttpMuxWebhookStatusProvider(
                    httpClient,
                    endpointPathFormat: "/muxpackageauthapi/us-central1/getMuxWebhookStatus?uploadId={0}",
                    getBearerTokenAsync: ct => Task.FromResult(firebaseToken ?? string.Empty));
            }
            else
            {
                detailsProvider = new HttpMuxUploadDetailsProvider(
                    httpClient,
                    endpointPathFormat: "/muxpackageauthapi/us-central1/getMuxUploadStatus?uploadId={0}",
                    getBearerTokenAsync: ct => Task.FromResult(firebaseToken ?? string.Empty));
            }
        }

        var uploader = new MuxDirectUploader(httpClient, authProvider, detailsProvider);
        var progress = new Progress<MuxUploadProgress>(p =>
        {
            var totalPart = p.TotalBytes.HasValue ? $"/{p.TotalBytes}" : "";
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (p.Percent is { } percent)
                {
                    UploadProgressBar.Progress = Math.Clamp(percent / 100.0, 0, 1);
                    ProgressLabel.Text = $"Progress: {percent:F2}% ({p.BytesSent}{totalPart} bytes)";
                }
                else
                {
                    ProgressLabel.Text = $"Progress: {p.BytesSent}{totalPart} bytes uploaded";
                }
            });
        });

        return new UploadUiSetup(uploader, progress, fetchDetailsAfterPut, useWebhookStatus);
    }

    private bool TryBuildAuthContext(out MuxAuthRequestContext? authContext, out string? errorMessage)
    {
        authContext = null;
        errorMessage = null;
        var c = CreatorIdEntry.Text?.Trim();
        var ex = ExternalIdEntry.Text?.Trim();
        var metaJson = MetadataJsonEntry.Text?.Trim();
        IReadOnlyDictionary<string, string>? metadata = null;
        if (!string.IsNullOrWhiteSpace(metaJson))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson);
            }
            catch (JsonException jex)
            {
                errorMessage = $"metadata JSON invalid - {jex.Message}";
                return false;
            }
        }

        if (!string.IsNullOrEmpty(c) || !string.IsNullOrEmpty(ex) || metadata is { Count: > 0 })
        {
            authContext = new MuxAuthRequestContext
            {
                CreatorId = string.IsNullOrEmpty(c) ? null : c,
                ExternalId = string.IsNullOrEmpty(ex) ? null : ex,
                Metadata = metadata,
            };
        }

        return true;
    }

    private void ClearResultLabels()
    {
        ResultAuthPutUriLabel.Text = "—";
        ResultAuthUploadIdLabel.Text = "—";
        ResultAuthAssetIdLabel.Text = "—";
        ResultAuthPlaybackIdLabel.Text = "—";
        ResultMuxGetHintLabel.Text = "Run an upload to see webhook snapshot or Mux GET details here.";
        ResultMuxIdLabel.Text = "—";
        ResultMuxStatusLabel.Text = "—";
        ResultMuxLastEventTypeLabel.Text = "—";
        ResultMuxAssetIdLabel.Text = "—";
        ResultMuxNasPassthroughLabel.Text = "—";
        ResultMuxNasMetaLabel.Text = "—";
        ResultMuxPlaybackIdLabel.Text = "—";
        ResultMuxAssetStatusLabel.Text = "—";
        ResultMuxAssetMetaLabel.Text = "—";
        ResultMuxErrorLabel.Text = "—";
    }

    private void ApplyOutcomeToLabels(MuxUploadOutcome outcome, bool fetchDetailsAfterPut, bool useWebhookStatus)
    {
        var a = outcome.Auth;
        ResultAuthPutUriLabel.Text = TruncateForUi(a.PutUri.ToString(), 120);
        ResultAuthUploadIdLabel.Text = a.UploadId ?? "—";
        ResultAuthAssetIdLabel.Text = a.AssetId ?? "—";
        ResultAuthPlaybackIdLabel.Text = a.PlaybackId ?? "—";

        if (!fetchDetailsAfterPut)
        {
            ResultMuxGetHintLabel.Text =
                "Details fetch not used (enable for Firebase-style base URL + ID token).";
            ResultMuxIdLabel.Text = "—";
            ResultMuxStatusLabel.Text = "—";
            ResultMuxLastEventTypeLabel.Text = "—";
            ResultMuxAssetIdLabel.Text = "—";
            ResultMuxNasPassthroughLabel.Text = "—";
            ResultMuxNasMetaLabel.Text = "—";
            ResultMuxPlaybackIdLabel.Text = "—";
            ResultMuxAssetStatusLabel.Text = "—";
            ResultMuxAssetMetaLabel.Text = "—";
            ResultMuxErrorLabel.Text = "—";
            return;
        }

        if (string.IsNullOrWhiteSpace(a.UploadId))
        {
            ResultMuxGetHintLabel.Text =
                "Skipped: auth response had no uploadId — deploy a backend that returns uploadId next to uploadUrl.";
            ResultMuxIdLabel.Text = "—";
            ResultMuxStatusLabel.Text = "—";
            ResultMuxLastEventTypeLabel.Text = "—";
            ResultMuxAssetIdLabel.Text = "—";
            ResultMuxNasPassthroughLabel.Text = "—";
            ResultMuxNasMetaLabel.Text = "—";
            ResultMuxPlaybackIdLabel.Text = "—";
            ResultMuxAssetStatusLabel.Text = "—";
            ResultMuxAssetMetaLabel.Text = "—";
            ResultMuxErrorLabel.Text = "—";
            return;
        }

        if (outcome.UploadDetails is null)
        {
            ResultMuxGetHintLabel.Text = useWebhookStatus
                ? "GET getMuxWebhookStatus failed or timed out (check token, deploy function, Mux webhook → Firestore, or 404 until first event)."
                : "GET getMuxUploadStatus returned no body or failed (check token, deploy getMuxUploadStatus, or 404).";
            ResultMuxIdLabel.Text = "—";
            ResultMuxStatusLabel.Text = "—";
            ResultMuxLastEventTypeLabel.Text = "—";
            ResultMuxAssetIdLabel.Text = "—";
            ResultMuxNasPassthroughLabel.Text = "—";
            ResultMuxNasMetaLabel.Text = "—";
            ResultMuxPlaybackIdLabel.Text = "—";
            ResultMuxAssetStatusLabel.Text = "—";
            ResultMuxAssetMetaLabel.Text = "—";
            ResultMuxErrorLabel.Text = "—";
            return;
        }

        var d = outcome.UploadDetails;
        var hint = useWebhookStatus
            ? "Loaded via HttpMuxWebhookStatusProvider → getMuxWebhookStatus (Firestore; Mux webhook must be configured)."
            : "Loaded via HttpMuxUploadDetailsProvider → getMuxUploadStatus (+ asset merge for playback).";
        if (!useWebhookStatus && !string.IsNullOrEmpty(d.AssetId) && string.IsNullOrEmpty(d.PlaybackId))
        {
            hint += " Playback id may appear after asset_status is ready — retry or wait.";
        }

        ResultMuxGetHintLabel.Text = hint;
        ResultMuxIdLabel.Text = d.Id ?? "—";
        ResultMuxStatusLabel.Text = d.Status ?? "—";
        ResultMuxLastEventTypeLabel.Text = d.LastEventType ?? "—";
        ResultMuxAssetIdLabel.Text = d.AssetId ?? "—";

        var nas = d.NewAssetSettings;
        ResultMuxNasPassthroughLabel.Text = nas?.Passthrough ?? "—";
        ResultMuxNasMetaLabel.Text = FormatStringDictionary(nas?.Meta);

        ResultMuxPlaybackIdLabel.Text = d.PlaybackId ?? "—";
        ResultMuxAssetStatusLabel.Text = d.AssetStatus ?? "—";
        ResultMuxAssetMetaLabel.Text = FormatStringDictionary(d.AssetMeta);

        if (d.Error is { } err)
        {
            var parts = new[] { err.Type, err.Message }.Where(s => !string.IsNullOrWhiteSpace(s));
            ResultMuxErrorLabel.Text = string.Join(" — ", parts);
            if (string.IsNullOrWhiteSpace(ResultMuxErrorLabel.Text))
                ResultMuxErrorLabel.Text = "(error object present)";
        }
        else
        {
            ResultMuxErrorLabel.Text = "—";
        }
    }

    private static string TruncateForUi(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";

    private static string FormatStringDictionary(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0)
            return "—";
        return string.Join("\n", map.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    private static bool IsPathUnderDirectory(string path, string directoryRoot)
    {
        try
        {
            var root = Path.GetFullPath(directoryRoot.TrimEnd(Path.DirectorySeparatorChar));
            var p = Path.GetFullPath(path);
            return p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || p.Equals(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gallery / temp picker paths may disappear after restart — copy into app data for persisted uploads.
    /// </summary>
    private static async Task<string> EnsureStableLocalVideoCopyAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var appData = Path.GetFullPath(FileSystem.AppDataDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (IsPathUnderDirectory(sourcePath, appData))
            return Path.GetFullPath(sourcePath);

        var destDir = Path.Combine(FileSystem.AppDataDirectory, "mux_uploads");
        Directory.CreateDirectory(destDir);
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext))
            ext = ".mp4";
        var dest = Path.Combine(destDir, $"{Guid.NewGuid():N}{ext}");

        await using (var src = new FileStream(
                           sourcePath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           bufferSize: 1024 * 1024,
                           options: FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var dst = new FileStream(
                           dest,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 1024 * 1024,
                           options: FileOptions.Asynchronous))
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);

        return Path.GetFullPath(dest);
    }

#if WINDOWS
    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
#endif

    private async void OnPickVideoClicked(object? sender, EventArgs e)
    {
        try
        {
            FileResult? result;

#if WINDOWS
            Exception? windowsPickerError = null;
            Exception? windowsFallbackError = null;
            try
            {
                // Prefer MediaPicker first for behavior parity across platforms.
#pragma warning disable CS0618 // Keep single-file API as first attempt on Windows
                result = await MediaPicker.Default.PickVideoAsync();
#pragma warning restore CS0618
            }
            catch (Exception ex)
            {
                windowsPickerError = ex;
                result = null;
            }

            // Fallback: use native WinRT picker with HWND init for unpackaged reliability.
            if (result is null)
            {
                try
                {
                    result = await WindowsVideoPickHelper.PickVideoAsync();
                }
                catch (Exception ex)
                {
                    windowsFallbackError = ex;
                    if (windowsPickerError is null)
                        windowsPickerError = windowsFallbackError;
                }
            }

            if (result is null)
            {
                if (windowsPickerError is not null || windowsFallbackError is not null)
                {
                    static string FormatException(string prefix, Exception ex)
                    {
                        var inner = ex.InnerException?.Message;
                        var detail = string.IsNullOrWhiteSpace(inner)
                            ? ex.ToString()
                            : $"{ex}\nINNER: {inner}";
                        return $"{prefix}: {detail}";
                    }

                    var parts = new List<string>();
                    if (windowsPickerError is not null)
                        parts.Add(FormatException("MediaPicker", windowsPickerError));
                    if (windowsFallbackError is not null)
                        parts.Add(FormatException("WindowsVideoPickHelper", windowsFallbackError));

                    var adminHint = IsRunningAsAdministrator()
                        ? " | hint: app appears to be running as Administrator; WinUI picker COM APIs often fail in unpackaged admin mode. Run the app as a normal user."
                        : " | hint: if this app is running as Administrator, close it and run as normal user.";
                    StatusLabel.Text = $"Status: pick failed - {string.Join(" || ", parts)}{adminHint}";
                }
                else
                {
                    StatusLabel.Text =
                        "Status: pick failed - both MediaPicker and WindowsVideoPickHelper returned no file and no exception";
                }

                return;
            }
#else
            var multi = await MediaPicker.Default.PickVideosAsync();
            result = multi?.FirstOrDefault();
            if (result is null)
            {
#pragma warning disable CS0618 // Single-file fallback until MAUI fixes PickVideosAsync on all targets
                result = await MediaPicker.Default.PickVideoAsync();
#pragma warning restore CS0618
            }
#endif

            if (result is null)
            {
                StatusLabel.Text = "Status: pick canceled or no file returned by picker";
                return;
            }

            _pickedVideo = result;
            SelectedFileLabel.Text = $"Selected: {result.FileName ?? "video"}";
            StartUploadButton.IsEnabled = true;

            var path = !string.IsNullOrWhiteSpace(result.FullPath) && File.Exists(result.FullPath)
                ? Path.GetFullPath(result.FullPath)
                : null;

            MuxResumableUploadSession? saved = null;
            try
            {
                saved = await TryLoadSessionAsync();
            }
            catch
            {
                /* Session store must not block picking if SQLite fails on this device. */
            }

            if (saved is not null && path is not null
                && !string.Equals(saved.LocalFilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await ClearSessionAsync();
                }
                catch
                {
                    /* ignore */
                }

                _ = RefreshResumeSavedUploadUiAsync();
            }

            StatusLabel.Text = "Status: video selected";
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException?.Message;
            var detail = string.IsNullOrWhiteSpace(inner)
                ? ex.ToString()
                : $"{ex}\nINNER: {inner}";
            StatusLabel.Text = $"Status: pick failed - {detail}";
        }
    }

    private async void OnStartUploadClicked(object? sender, EventArgs e)
    {
        if (_pickedVideo is null)
        {
            StatusLabel.Text = "Status: pick a video first";
            return;
        }

        if (!Uri.TryCreate(BackendBaseUrlEntry.Text?.Trim(), UriKind.Absolute, out var backendBaseUri))
        {
            StatusLabel.Text = "Status: backend base URL is invalid";
            return;
        }

        /*var looksLikeFirebaseFunction = (BackendBaseUrlEntry.Text ?? "").Contains("cloudfunctions.net", StringComparison.OrdinalIgnoreCase)
            || (BackendBaseUrlEntry.Text ?? "").Contains(".run.app", StringComparison.OrdinalIgnoreCase);*/
        var looksLikeFirebaseFunction = true;
    /*    if (looksLikeFirebaseFunction && string.IsNullOrWhiteSpace(firebaseToken))
        {
            StatusLabel.Text = "Status: paste a Firebase ID token";
            return;
        }*/

        try
        {
            StartUploadButton.IsEnabled = false;
            ResumeSavedUploadButton.IsEnabled = false;
            CancelUploadButton.IsEnabled = true;
            PauseUploadButton.IsEnabled = false;
            ResumeUploadButton.IsEnabled = false;
            PickFileButton.IsEnabled = false;
            MainThread.BeginInvokeOnMainThread(ClearResultLabels);
            StatusLabel.Text = "Status: opening video and requesting upload URL...";
            UploadProgressBar.Progress = 0;
            ProgressLabel.Text = "Progress: 0%";

            var localFilePath = !string.IsNullOrWhiteSpace(_pickedVideo.FullPath) && File.Exists(_pickedVideo.FullPath)
                ? _pickedVideo.FullPath
                : null;
            Stream? videoStream = null;

            using var httpClient = new HttpClient { BaseAddress = backendBaseUri };
            // Default HttpClient.Timeout is often 100s — large videos need a longer limit or uploads abort mid-stream.
            httpClient.Timeout = TimeSpan.FromMilliseconds(-1);

            var setup = CreateUploadSetupForPage(httpClient, looksLikeFirebaseFunction);

            if (!TryBuildAuthContext(out var authContext, out var authFormError))
            {
                StatusLabel.Text = $"Status: {authFormError}";
                return;
            }

            var contentType = string.IsNullOrWhiteSpace(ContentTypeEntry.Text) ? null : ContentTypeEntry.Text.Trim();
            (MuxUploadHandle handle, Task<MuxUploadOutcome> uploadTask) upload;
            if (localFilePath is not null)
            {
                await ClearSessionAsync();
                MainThread.BeginInvokeOnMainThread(() =>
                    StatusLabel.Text = "Status: copying video to app storage (if needed)…");
                localFilePath = await EnsureStableLocalVideoCopyAsync(localFilePath, CancellationToken.None);
                MainThread.BeginInvokeOnMainThread(() =>
                    SelectedFileLabel.Text = $"Selected (stable path): {Path.GetFileName(localFilePath)}");

                StatusLabel.Text = "Status: requesting upload URL (persisted resumable)...";
                var session = await setup.Uploader.CreatePersistedUploadSessionAsync(
                    localFilePath,
                    contentType: contentType,
                    authContext: authContext);
                await SaveSessionAsync(session);
                upload = setup.Uploader.ContinuePersistedResumableUploadAsync(
                    session,
                    PersistSessionAsync,
                    setup.Progress,
                    authContextForReauth: authContext);
            }
            else
            {
                videoStream = await _pickedVideo.OpenReadAsync();
                upload = videoStream.CanSeek
                    ? setup.Uploader.StartResumableUploadAsync(
                        videoStream,
                        contentType: contentType,
                        leaveOpen: false,
                        progress: setup.Progress,
                        authContext: authContext)
                    : setup.Uploader.StartUploadAsync(
                        videoStream,
                        contentLength: null,
                        contentType: contentType,
                        leaveOpen: false,
                        progress: setup.Progress,
                        authContext: authContext);
            }

            var (handle, uploadTask) = upload;
            _currentUploadHandle = handle;
            PauseUploadButton.IsEnabled = handle.CanPause;
            ResumeUploadButton.IsEnabled = false;
            StatusLabel.Text = handle.CanPause
                ? "Status: resumable upload started"
                : "Status: upload started (stream is not seekable; pause/resume disabled)";

            var outcome = await uploadTask;

            if (localFilePath is not null)
                await ClearSessionAsync();

            MainThread.BeginInvokeOnMainThread(() => ApplyOutcomeToLabels(outcome, setup.FetchDetailsAfterPut, setup.UseWebhookStatus));

            var a = outcome.Auth;
            StatusLabel.Text =
                $"Status: upload completed — auth uploadId={a.UploadId ?? "—"}, Mux GET status={outcome.UploadDetails?.Status ?? "n/a"}";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Status: upload canceled";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Status: upload failed - {ex.Message}";
        }
        finally
        {
            _currentUploadHandle = null;
            CancelUploadButton.IsEnabled = false;
            PauseUploadButton.IsEnabled = false;
            ResumeUploadButton.IsEnabled = false;
            PickFileButton.IsEnabled = true;
            StartUploadButton.IsEnabled = _pickedVideo is not null;
            _ = RefreshResumeSavedUploadUiAsync();
        }
    }

    private async void OnResumeSavedUploadClicked(object? sender, EventArgs e)
    {
        var session = await TryLoadSessionAsync();
        if (session is null || string.IsNullOrWhiteSpace(session.LocalFilePath) || !File.Exists(session.LocalFilePath))
        {
            StatusLabel.Text = "Status: no saved session or local file is missing";
            await RefreshResumeSavedUploadUiAsync();
            return;
        }

        if (!Uri.TryCreate(BackendBaseUrlEntry.Text?.Trim(), UriKind.Absolute, out var backendBaseUri))
        {
            StatusLabel.Text = "Status: backend base URL is invalid";
            return;
        }

        try
        {
            ResumeSavedUploadButton.IsEnabled = false;
            StartUploadButton.IsEnabled = false;
            CancelUploadButton.IsEnabled = true;
            PauseUploadButton.IsEnabled = false;
            ResumeUploadButton.IsEnabled = false;
            PickFileButton.IsEnabled = false;
            MainThread.BeginInvokeOnMainThread(ClearResultLabels);
            StatusLabel.Text = "Status: resuming persisted upload (probing Mux offset)...";
            UploadProgressBar.Progress = 0;
            ProgressLabel.Text = "Progress: 0%";

            using var httpClient = new HttpClient { BaseAddress = backendBaseUri };
            httpClient.Timeout = TimeSpan.FromMilliseconds(-1);

            if (!TryBuildAuthContext(out var resumeAuthContext, out var resumeAuthErr))
            {
                StatusLabel.Text = $"Status: {resumeAuthErr}";
                return;
            }

            var setup = CreateUploadSetupForPage(httpClient, looksLikeFirebaseFunction: true);
            var upload = setup.Uploader.ContinuePersistedResumableUploadAsync(
                session,
                PersistSessionAsync,
                setup.Progress,
                authContextForReauth: resumeAuthContext);

            var (handle, uploadTask) = upload;
            _currentUploadHandle = handle;
            PauseUploadButton.IsEnabled = handle.CanPause;
            ResumeUploadButton.IsEnabled = false;
            StatusLabel.Text = "Status: resumable upload resumed from saved session";

            var outcome = await uploadTask;

            await ClearSessionAsync();

            MainThread.BeginInvokeOnMainThread(() => ApplyOutcomeToLabels(outcome, setup.FetchDetailsAfterPut, setup.UseWebhookStatus));

            var a = outcome.Auth;
            StatusLabel.Text =
                $"Status: upload completed — auth uploadId={a.UploadId ?? "—"}, Mux GET status={outcome.UploadDetails?.Status ?? "n/a"}";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Status: upload canceled";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Status: upload failed - {ex.Message}";
        }
        finally
        {
            _currentUploadHandle = null;
            CancelUploadButton.IsEnabled = false;
            PauseUploadButton.IsEnabled = false;
            ResumeUploadButton.IsEnabled = false;
            PickFileButton.IsEnabled = true;
            StartUploadButton.IsEnabled = _pickedVideo is not null;
            _ = RefreshResumeSavedUploadUiAsync();
        }
    }

    private void OnCancelUploadClicked(object? sender, EventArgs e)
    {
        _currentUploadHandle?.Cancel();
    }

    private void OnPauseUploadClicked(object? sender, EventArgs e)
    {
        if (_currentUploadHandle is null || !_currentUploadHandle.CanPause)
            return;

        _currentUploadHandle.Pause();
        PauseUploadButton.IsEnabled = false;
        ResumeUploadButton.IsEnabled = true;
        StatusLabel.Text = "Status: upload pausing after current chunk...";
    }

    private void OnResumeUploadClicked(object? sender, EventArgs e)
    {
        if (_currentUploadHandle is null || !_currentUploadHandle.CanPause)
            return;

        _currentUploadHandle.Resume();
        PauseUploadButton.IsEnabled = true;
        ResumeUploadButton.IsEnabled = false;
        StatusLabel.Text = "Status: upload resumed";
    }
}
