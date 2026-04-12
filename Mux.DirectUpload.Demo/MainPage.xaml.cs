using System.Linq;
using Mux.DirectUpload.Maui;

namespace Mux.DirectUpload.Demo;

public partial class MainPage : ContentPage
{
    private FileResult? _pickedVideo;
    private MuxUploadHandle? _currentUploadHandle;

    public MainPage()
    {
        InitializeComponent();
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

        try
        {
            StartUploadButton.IsEnabled = false;
            CancelUploadButton.IsEnabled = true;
            PickFileButton.IsEnabled = false;
            StatusLabel.Text = "Status: opening video and requesting upload URL...";
            UploadProgressBar.Progress = 0;
            ProgressLabel.Text = "Progress: 0%";

            var videoStream = await _pickedVideo.OpenReadAsync();

            using var httpClient = new HttpClient { BaseAddress = backendBaseUri };
            var authProvider = new HttpMuxAuthUrlProvider(
                httpClient,
                endpointPath: string.IsNullOrWhiteSpace(EndpointPathEntry.Text)
                    ? "/api/mux/direct-upload-url"
                    : EndpointPathEntry.Text.Trim());

            var uploader = new MuxDirectUploader(httpClient, authProvider);
            var progress = new Progress<MuxUploadProgress>(p =>
            {
                var percent = p.Percent ?? 0;
                var totalPart = p.TotalBytes.HasValue ? $"/{p.TotalBytes}" : "";
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    UploadProgressBar.Progress = Math.Clamp(percent / 100.0, 0, 1);
                    ProgressLabel.Text = $"Progress: {percent:F2}% ({p.BytesSent}{totalPart} bytes)";
                });
            });

            var (handle, uploadTask) = uploader.StartUploadAsync(
                videoStream,
                contentType: string.IsNullOrWhiteSpace(ContentTypeEntry.Text) ? null : ContentTypeEntry.Text.Trim(),
                leaveOpen: false,
                progress: progress);

            _currentUploadHandle = handle;
            StatusLabel.Text = "Status: upload started";

            await uploadTask;

            StatusLabel.Text = "Status: upload completed";
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
            PickFileButton.IsEnabled = true;
            StartUploadButton.IsEnabled = _pickedVideo is not null;
        }
    }

    private void OnCancelUploadClicked(object? sender, EventArgs e)
    {
        _currentUploadHandle?.Cancel();
    }
}
