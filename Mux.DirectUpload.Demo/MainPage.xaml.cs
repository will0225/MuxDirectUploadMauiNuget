using System.Linq;
using System.Text.Json;
using Mux.DirectUpload.Maui;

namespace Mux.DirectUpload.Demo;

public partial class MainPage : ContentPage
{
    private FileResult? _pickedVideo;
    private MuxUploadHandle? _currentUploadHandle;

    public MainPage()
    {
        InitializeComponent();
        ClearResultLabels();
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

    private async void OnPickVideoClicked(object? sender, EventArgs e)
    {
        try
        {
            var results = await MediaPicker.Default.PickVideosAsync();
            var result = results?.FirstOrDefault();

            if (result is null)
                return;

            _pickedVideo = result;
            SelectedFileLabel.Text = $"Selected: {result.FileName ?? "video"}";
            StartUploadButton.IsEnabled = true;
            StatusLabel.Text = "Status: video selected";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Status: pick failed - {ex.Message}";
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

        var baseUrlText = BackendBaseUrlEntry.Text?.Trim() ?? "";
        /*var looksLikeFirebaseFunction = baseUrlText.Contains("cloudfunctions.net", StringComparison.OrdinalIgnoreCase)
            || baseUrlText.Contains(".run.app", StringComparison.OrdinalIgnoreCase);*/
        var looksLikeFirebaseFunction = true;
        var firebaseToken = FirebaseIdTokenEntry.Text?.Trim();
    /*    if (looksLikeFirebaseFunction && string.IsNullOrWhiteSpace(firebaseToken))
        {
            StatusLabel.Text = "Status: paste a Firebase ID token";
            return;
        }*/

        try
        {
            StartUploadButton.IsEnabled = false;
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

            var authEndpointPath = string.IsNullOrWhiteSpace(EndpointPathEntry.Text)
                ? "/muxpackageauthapi/us-central1/getMuxDirectUploadUrl"
                : EndpointPathEntry.Text.Trim();

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

            MuxAuthRequestContext? authContext = null;
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
                    StatusLabel.Text = $"Status: metadata JSON invalid - {jex.Message}";
                    return;
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

            var contentType = string.IsNullOrWhiteSpace(ContentTypeEntry.Text) ? null : ContentTypeEntry.Text.Trim();
            (MuxUploadHandle handle, Task<MuxUploadOutcome> uploadTask) upload;
            if (localFilePath is not null)
            {
                upload = uploader.StartResumableUploadAsync(
                    localFilePath,
                    contentType: contentType,
                    progress: progress,
                    authContext: authContext);
            }
            else
            {
                videoStream = await _pickedVideo.OpenReadAsync();
                upload = videoStream.CanSeek
                    ? uploader.StartResumableUploadAsync(
                        videoStream,
                        contentType: contentType,
                        leaveOpen: false,
                        progress: progress,
                        authContext: authContext)
                    : uploader.StartUploadAsync(
                    videoStream,
                    contentLength: null,
                    contentType: contentType,
                    leaveOpen: false,
                    progress: progress,
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

            MainThread.BeginInvokeOnMainThread(() => ApplyOutcomeToLabels(outcome, fetchDetailsAfterPut, useWebhookStatus));

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
