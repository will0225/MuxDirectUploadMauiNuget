using Mux.DirectUpload.Maui;

namespace Mux.DirectUpload.Demo;

public partial class MainPage : ContentPage
{
    private string? _selectedFilePath;
    private MuxUploadHandle? _currentUploadHandle;

    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnPickFileClicked(object? sender, EventArgs e)
    {
        try
        {
            var pickResult = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Pick a video file for Mux upload"
            });

            if (pickResult is null)
                return;

            _selectedFilePath = pickResult.FullPath;
            SelectedFileLabel.Text = $"Selected: {_selectedFilePath}";
            StartUploadButton.IsEnabled = !string.IsNullOrWhiteSpace(_selectedFilePath);
            StatusLabel.Text = "Status: file selected";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Status: file pick failed - {ex.Message}";
        }
    }

    private async void OnStartUploadClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedFilePath))
        {
            StatusLabel.Text = "Status: select a video file first";
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
            StatusLabel.Text = "Status: requesting authenticated upload URL...";
            UploadProgressBar.Progress = 0;
            ProgressLabel.Text = "Progress: 0%";

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
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    UploadProgressBar.Progress = Math.Clamp(percent / 100.0, 0, 1);
                    ProgressLabel.Text = $"Progress: {percent:F2}% ({p.BytesSent}/{p.TotalBytes})";
                });
            });

            var (handle, uploadTask) = uploader.StartUploadAsync(
                filePath: _selectedFilePath,
                contentType: string.IsNullOrWhiteSpace(ContentTypeEntry.Text) ? null : ContentTypeEntry.Text.Trim(),
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
            StartUploadButton.IsEnabled = !string.IsNullOrWhiteSpace(_selectedFilePath);
        }
    }

    private void OnCancelUploadClicked(object? sender, EventArgs e)
    {
        _currentUploadHandle?.Cancel();
    }
}
