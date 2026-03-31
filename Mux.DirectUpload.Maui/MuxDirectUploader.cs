using System.Net;
using System.Net.Http.Headers;

namespace Mux.DirectUpload.Maui;

public sealed class MuxDirectUploader
{
    private readonly HttpClient _httpClient;
    private readonly IMuxAuthUrlProvider _authUrlProvider;

    public MuxDirectUploader(HttpClient httpClient, IMuxAuthUrlProvider authUrlProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authUrlProvider = authUrlProvider ?? throw new ArgumentNullException(nameof(authUrlProvider));
    }

    /// <summary>
    /// Starts a direct upload to Mux using an authenticated PUT URL fetched via IMuxAuthUrlProvider.
    /// </summary>
    public (MuxUploadHandle handle, Task uploadTask) StartUploadAsync(
        string filePath,
        int bufferSizeBytes = 1024 * 1024,
        string? contentType = null,
        IProgress<MuxUploadProgress>? progress = null,
        CancellationToken externalToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be provided.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Video file not found.", filePath);
        if (bufferSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSizeBytes), "Buffer size must be positive.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var task = UploadInternalAsync(filePath, bufferSizeBytes, contentType, progress, cts.Token);
        return (new MuxUploadHandle(cts), task);
    }

    private async Task UploadInternalAsync(
        string filePath,
        int bufferSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var uploadUrl = await _authUrlProvider.GetUploadUrlAsync(cancellationToken);

        var fileInfo = new FileInfo(filePath);
        var totalBytes = fileInfo.Length;

        await using var fileStream = File.OpenRead(filePath);

        using var content = new ProgressableStreamContent(
            fileStream,
            bufferSizeBytes,
            sent => progress?.Report(new MuxUploadProgress(sent, totalBytes)),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(contentType))
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = content
        };

        request.Headers.ExpectContinue = false;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken);
            throw new HttpRequestException($"Upload unauthorized/forbidden. Status={(int)response.StatusCode}. Body={body}");
        }

        response.EnsureSuccessStatusCode();
        progress?.Report(new MuxUploadProgress(totalBytes, totalBytes));
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return "<failed to read body>"; }
    }

    private sealed class ProgressableStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly int _bufferSize;
        private readonly Action<long> _onProgress;
        private readonly CancellationToken _ct;

        public ProgressableStreamContent(Stream source, int bufferSize, Action<long> onProgress, CancellationToken ct)
        {
            _source = source;
            _bufferSize = bufferSize;
            _onProgress = onProgress;
            _ct = ct;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[_bufferSize];
            long totalSent = 0;

            while (true)
            {
                _ct.ThrowIfCancellationRequested();

                var read = await _source.ReadAsync(buffer.AsMemory(0, buffer.Length), _ct);
                if (read == 0)
                    break;

                await stream.WriteAsync(buffer.AsMemory(0, read), _ct);
                totalSent += read;
                _onProgress(totalSent);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_source.CanSeek)
            {
                length = _source.Length;
                return true;
            }

            length = -1;
            return false;
        }
    }
}

