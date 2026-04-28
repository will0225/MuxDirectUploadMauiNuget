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
    private const int DefaultResumableChunkSizeBytes = 8 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IMuxAuthUrlProvider _authUrlProvider;
    private readonly IMuxUploadDetailsProvider? _uploadDetailsProvider;

    public MuxDirectUploader(HttpClient httpClient, IMuxAuthUrlProvider authUrlProvider)
        : this(httpClient, authUrlProvider, uploadDetailsProvider: null)
    {
    }

    /// <param name="uploadDetailsProvider">If set, after a successful PUT the library calls this with <see cref="MuxAuthUrlResult.UploadId"/> so your backend can return Mux GET upload details (do not call Mux from the app with API tokens).</param>
    public MuxDirectUploader(
        HttpClient httpClient,
        IMuxAuthUrlProvider authUrlProvider,
        IMuxUploadDetailsProvider? uploadDetailsProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authUrlProvider = authUrlProvider ?? throw new ArgumentNullException(nameof(authUrlProvider));
        _uploadDetailsProvider = uploadDetailsProvider;
    }

    /// <summary>
    /// Starts a direct upload to Mux using an authenticated PUT URL fetched via IMuxAuthUrlProvider.
    /// </summary>
    /// <param name="authContext">Optional metadata forwarded to your auth-url GET (headers and query).</param>
    /// <returns>The upload task completes with <see cref="MuxUploadOutcome"/> after a successful upload.</returns>
    public (MuxUploadHandle handle, Task<MuxUploadOutcome> uploadTask) StartUploadAsync(
        string filePath,
        int bufferSizeBytes = 1024 * 1024,
        string? contentType = null,
        IProgress<MuxUploadProgress>? progress = null,
        CancellationToken externalToken = default,
        MuxAuthRequestContext? authContext = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be provided.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Video file not found.", filePath);
        if (bufferSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSizeBytes), "Buffer size must be positive.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var task = UploadFromFileAsync(filePath, bufferSizeBytes, contentType, progress, authContext, cts.Token);
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
    /// <param name="authContext">Optional metadata forwarded to your auth-url GET (headers and query).</param>
    /// <returns>The upload task completes with <see cref="MuxUploadOutcome"/> after a successful upload.</returns>
    public (MuxUploadHandle handle, Task<MuxUploadOutcome> uploadTask) StartUploadAsync(
        Stream stream,
        long? contentLength = null,
        int bufferSizeBytes = 1024 * 1024,
        string? contentType = null,
        bool leaveOpen = false,
        IProgress<MuxUploadProgress>? progress = null,
        CancellationToken externalToken = default,
        MuxAuthRequestContext? authContext = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (bufferSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSizeBytes), "Buffer size must be positive.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var task = UploadFromStreamAsync(stream, contentLength, leaveOpen, bufferSizeBytes, contentType, progress, authContext, cts.Token);
        return (new MuxUploadHandle(cts), task);
    }

    /// <summary>
    /// Starts a chunked Mux direct upload that can pause/resume between chunks.
    /// </summary>
    /// <remarks>
    /// Mux resumable uploads use repeated <c>PUT</c> requests with <c>Content-Range</c>.
    /// <see cref="MuxUploadHandle.Pause"/> stops after the current in-flight chunk completes; <see cref="MuxUploadHandle.Resume"/>
    /// continues with the next chunk. <see cref="MuxUploadHandle.Cancel"/> aborts the current request.
    /// </remarks>
    public (MuxUploadHandle handle, Task<MuxUploadOutcome> uploadTask) StartResumableUploadAsync(
        string filePath,
        int chunkSizeBytes = DefaultResumableChunkSizeBytes,
        string? contentType = null,
        IProgress<MuxUploadProgress>? progress = null,
        CancellationToken externalToken = default,
        MuxAuthRequestContext? authContext = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be provided.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Video file not found.", filePath);
        if (chunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), "Chunk size must be positive.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var pauseController = new MuxUploadPauseController();
        var task = UploadResumableFromFileAsync(filePath, chunkSizeBytes, contentType, progress, authContext, pauseController, cts.Token);
        return (new MuxUploadHandle(cts, pauseController), task);
    }

    /// <summary>
    /// Starts a chunked Mux direct upload from a seekable stream that can pause/resume between chunks.
    /// </summary>
    public (MuxUploadHandle handle, Task<MuxUploadOutcome> uploadTask) StartResumableUploadAsync(
        Stream stream,
        long? contentLength = null,
        int chunkSizeBytes = DefaultResumableChunkSizeBytes,
        string? contentType = null,
        bool leaveOpen = false,
        IProgress<MuxUploadProgress>? progress = null,
        CancellationToken externalToken = default,
        MuxAuthRequestContext? authContext = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new ArgumentException("Resumable upload requires a seekable stream. Use file-path upload or StartUploadAsync for non-seekable streams.", nameof(stream));
        if (chunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), "Chunk size must be positive.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var pauseController = new MuxUploadPauseController();
        var task = UploadResumableFromStreamAsync(stream, contentLength, leaveOpen, chunkSizeBytes, contentType, progress, authContext, pauseController, cts.Token);
        return (new MuxUploadHandle(cts, pauseController), task);
    }

    private async Task<MuxUploadOutcome> UploadFromFileAsync(
        string filePath,
        int bufferSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        MuxAuthRequestContext? authContext,
        CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(filePath);
        return await UploadCoreAsync(
            fileStream,
            disposeStream: false,
            explicitLength: null,
            bufferSizeBytes,
            contentType,
            progress,
            authContext,
            cancellationToken);
    }

    private Task<MuxUploadOutcome> UploadFromStreamAsync(
        Stream stream,
        long? explicitLength,
        bool leaveOpen,
        int bufferSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        MuxAuthRequestContext? authContext,
        CancellationToken cancellationToken) =>
        UploadCoreAsync(
            stream,
            disposeStream: !leaveOpen,
            explicitLength,
            bufferSizeBytes,
            contentType,
            progress,
            authContext,
            cancellationToken);

    private async Task<MuxUploadOutcome> UploadResumableFromFileAsync(
        string filePath,
        int chunkSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        MuxAuthRequestContext? authContext,
        MuxUploadPauseController pauseController,
        CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(filePath);
        return await UploadResumableCoreAsync(
            fileStream,
            disposeStream: false,
            explicitLength: null,
            chunkSizeBytes,
            contentType,
            progress,
            authContext,
            pauseController,
            cancellationToken);
    }

    private Task<MuxUploadOutcome> UploadResumableFromStreamAsync(
        Stream stream,
        long? explicitLength,
        bool leaveOpen,
        int chunkSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        MuxAuthRequestContext? authContext,
        MuxUploadPauseController pauseController,
        CancellationToken cancellationToken) =>
        UploadResumableCoreAsync(
            stream,
            disposeStream: !leaveOpen,
            explicitLength,
            chunkSizeBytes,
            contentType,
            progress,
            authContext,
            pauseController,
            cancellationToken);

    private async Task<MuxUploadOutcome> UploadCoreAsync(
        Stream sourceStream,
        bool disposeStream,
        long? explicitLength,
        int bufferSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        MuxAuthRequestContext? authContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var auth = await _authUrlProvider.GetUploadUrlAsync(authContext, cancellationToken);

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

            using var request = new HttpRequestMessage(HttpMethod.Put, auth.PutUri)
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

            MuxUploadDetails? details = null;
            if (_uploadDetailsProvider is not null && !string.IsNullOrWhiteSpace(auth.UploadId))
            {
                details = await _uploadDetailsProvider
                    .GetUploadDetailsAsync(auth.UploadId!, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new MuxUploadOutcome(auth, details);
        }
        finally
        {
            if (disposeStream)
                await sourceStream.DisposeAsync();
        }
    }

    private async Task<MuxUploadOutcome> UploadResumableCoreAsync(
        Stream sourceStream,
        bool disposeStream,
        long? explicitLength,
        int chunkSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        MuxAuthRequestContext? authContext,
        MuxUploadPauseController pauseController,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!sourceStream.CanSeek)
                throw new InvalidOperationException("Resumable upload requires a seekable stream.");

            var auth = await _authUrlProvider.GetUploadUrlAsync(authContext, cancellationToken);
            var totalBytes = ResolveTotalBytes(sourceStream, explicitLength)
                ?? throw new InvalidOperationException("Resumable upload requires a known content length.");

            long offset = 0;
            progress?.Report(new MuxUploadProgress(0, totalBytes));

            while (offset < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await pauseController.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);

                var chunkLength = Math.Min(chunkSizeBytes, totalBytes - offset);
                var end = offset + chunkLength - 1;

                using var content = new RangedStreamContent(
                    sourceStream,
                    offset,
                    chunkLength,
                    1024 * 1024,
                    sent => progress?.Report(new MuxUploadProgress(offset + sent, totalBytes)),
                    cancellationToken);

                content.Headers.ContentRange = new ContentRangeHeaderValue(offset, end, totalBytes);
                if (!string.IsNullOrWhiteSpace(contentType))
                    content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                using var request = new HttpRequestMessage(HttpMethod.Put, auth.PutUri)
                {
                    Content = content
                };
                request.Headers.ExpectContinue = false;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                    throw new HttpRequestException($"Upload unauthorized/forbidden. Status={(int)response.StatusCode}. Body={body}");
                }

                if (!response.IsSuccessStatusCode && (int)response.StatusCode != 308)
                {
                    var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                    throw new HttpRequestException($"Chunk upload failed. Status={(int)response.StatusCode}. Body={body}");
                }

                offset += chunkLength;
                progress?.Report(new MuxUploadProgress(offset, totalBytes));
            }

            MuxUploadDetails? details = null;
            if (_uploadDetailsProvider is not null && !string.IsNullOrWhiteSpace(auth.UploadId))
            {
                details = await _uploadDetailsProvider
                    .GetUploadDetailsAsync(auth.UploadId!, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new MuxUploadOutcome(auth, details);
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

    private sealed class RangedStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly long _start;
        private readonly long _length;
        private readonly int _bufferSize;
        private readonly Action<long> _onProgress;
        private readonly CancellationToken _ct;

        public RangedStreamContent(
            Stream source,
            long start,
            long length,
            int bufferSize,
            Action<long> onProgress,
            CancellationToken ct)
        {
            _source = source;
            _start = start;
            _length = length;
            _bufferSize = bufferSize;
            _onProgress = onProgress;
            _ct = ct;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[_bufferSize];
            long remaining = _length;
            long sent = 0;
            _source.Position = _start;

            while (remaining > 0)
            {
                _ct.ThrowIfCancellationRequested();

                var readLength = (int)Math.Min(buffer.Length, remaining);
                var read = await _source.ReadAsync(buffer.AsMemory(0, readLength), _ct).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Unexpected end of source stream while uploading chunk.");

                await stream.WriteAsync(buffer.AsMemory(0, read), _ct).ConfigureAwait(false);
                remaining -= read;
                sent += read;
                _onProgress(sent);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
