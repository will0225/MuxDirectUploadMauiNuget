using System.Net;
using System.Net.Http.Headers;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Uploads video bytes to a Mux direct-upload URL obtained via <see cref="IMuxAuthUrlProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HttpClient"/> defaults to a <b>100 second</b> request timeout on many platforms. Large files take longer to upload;
/// when that limit is hit, the connection aborts and you may see <see cref="IOException"/> inside the request body stream (e.g. while copying to the transport).
/// </para>
/// <para>
/// For long uploads, set a higher limit on the same <see cref="HttpClient"/> you pass in, for example
/// <c>httpClient.Timeout = Timeout.InfiniteTimeSpan</c> or <c>TimeSpan.FromHours(2)</c>.
/// </para>
/// </remarks>
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
        var task = UploadFromFileAsync(filePath, bufferSizeBytes, contentType, progress, cts.Token);
        return (new MuxUploadHandle(cts), task);
    }

    /// <summary>
    /// Starts a direct upload from a stream (e.g. MAUI MediaPicker <c>OpenReadAsync()</c> when no usable file path is available).
    /// </summary>
    /// <param name="stream">Stream to read. Disposed after the upload completes unless <paramref name="leaveOpen"/> is true.</param>
    /// <param name="contentLength">
    /// Total byte length when <paramref name="stream"/> is not seekable (so length cannot be discovered).
    /// Omit when the stream is seekable. Used for progress and for the HTTP Content-Length header when supported.
    /// </param>
    /// <param name="bufferSizeBytes">Buffer size for copying from <paramref name="stream"/>.</param>
    /// <param name="contentType">Optional Content-Type for the PUT body.</param>
    /// <param name="leaveOpen">If true, <paramref name="stream"/> is not disposed after upload.</param>
    /// <param name="progress">Reports bytes sent; <see cref="MuxUploadProgress.Percent"/> is null if total size is unknown.</param>
    /// <param name="externalToken">Cancellation token.</param>
    public (MuxUploadHandle handle, Task uploadTask) StartUploadAsync(
        Stream stream,
        long? contentLength = null,
        int bufferSizeBytes = 1024 * 1024,
        string? contentType = null,
        bool leaveOpen = false,
        IProgress<MuxUploadProgress>? progress = null,
        CancellationToken externalToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (bufferSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSizeBytes), "Buffer size must be positive.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var task = UploadFromStreamAsync(stream, contentLength, leaveOpen, bufferSizeBytes, contentType, progress, cts.Token);
        return (new MuxUploadHandle(cts), task);
    }

    private async Task UploadFromFileAsync(
        string filePath,
        int bufferSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(filePath);
        await UploadCoreAsync(
            fileStream,
            disposeStream: false,
            explicitLength: null,
            bufferSizeBytes,
            contentType,
            progress,
            cancellationToken);
    }

    private Task UploadFromStreamAsync(
        Stream stream,
        long? explicitLength,
        bool leaveOpen,
        int bufferSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        CancellationToken cancellationToken) =>
        UploadCoreAsync(
            stream,
            disposeStream: !leaveOpen,
            explicitLength,
            bufferSizeBytes,
            contentType,
            progress,
            cancellationToken);

    private async Task UploadCoreAsync(
        Stream sourceStream,
        bool disposeStream,
        long? explicitLength,
        int bufferSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var uploadUrl = await _authUrlProvider.GetUploadUrlAsync(cancellationToken);

            var totalForProgress = ResolveTotalBytes(sourceStream, explicitLength);
            var lengthForHttp = ResolveHttpContentLength(sourceStream, explicitLength);

            using var content = new ProgressableStreamContent(
                sourceStream,
                bufferSizeBytes,
                sent => progress?.Report(new MuxUploadProgress(sent, totalForProgress)),
                cancellationToken,
                lengthForHttp);

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

            if (totalForProgress.HasValue)
                progress?.Report(new MuxUploadProgress(totalForProgress.Value, totalForProgress.Value));
        }
        finally
        {
            if (disposeStream)
                await sourceStream.DisposeAsync();
        }
    }

    private static long? ResolveTotalBytes(Stream stream, long? explicitLength)
    {
        if (stream.CanSeek)
            return stream.Length;
        return explicitLength;
    }

    private static long? ResolveHttpContentLength(Stream stream, long? explicitLength)
    {
        if (stream.CanSeek)
            return stream.Length;
        return explicitLength;
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
        private readonly long? _knownLength;

        public ProgressableStreamContent(
            Stream source,
            int bufferSize,
            Action<long> onProgress,
            CancellationToken ct,
            long? knownLength)
        {
            _source = source;
            _bufferSize = bufferSize;
            _onProgress = onProgress;
            _ct = ct;
            _knownLength = knownLength;
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
            if (_knownLength.HasValue)
            {
                length = _knownLength.Value;
                return true;
            }

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
