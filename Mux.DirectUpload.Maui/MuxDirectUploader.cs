using System.Globalization;
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

    private readonly record struct ProbeMuxResumeResult(long NextOffset, bool Unauthorized);

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

    /// <summary>
    /// Creates auth-backed session state for a resumable upload. Persist <see cref="MuxResumableUploadSession"/> (JSON, SQLite, etc.),
    /// then call <see cref="ContinuePersistedResumableUploadAsync"/> after process restart.
    /// </summary>
    public async Task<MuxResumableUploadSession> CreatePersistedUploadSessionAsync(
        string filePath,
        int chunkSizeBytes = DefaultResumableChunkSizeBytes,
        string? contentType = null,
        MuxAuthRequestContext? authContext = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be provided.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Video file not found.", filePath);
        if (chunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), "Chunk size must be positive.");

        var auth = await _authUrlProvider.GetUploadUrlAsync(authContext, cancellationToken).ConfigureAwait(false);
        var info = new FileInfo(filePath);
        return new MuxResumableUploadSession
        {
            PutUri = auth.PutUri.AbsoluteUri,
            UploadId = auth.UploadId,
            AssetId = auth.AssetId,
            PlaybackId = auth.PlaybackId,
            LocalFilePath = Path.GetFullPath(filePath),
            FileSizeBytes = info.Length,
            ChunkSizeBytes = chunkSizeBytes,
            ContentType = contentType,
            BytesUploadedSoFar = 0,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Continues a persisted resumable upload: probes Mux for the current byte offset, uploads remaining chunks,
    /// and invokes <paramref name="persistSessionAsync"/> after each successful chunk (and after probe).
    /// </summary>
    /// <param name="authContextForReauth">
    /// When set, an expired or invalid signed PUT URL (<c>401</c>/<c>403</c> on probe or chunk) triggers one automatic
    /// <see cref="IMuxAuthUrlProvider.GetUploadUrlAsync"/> call; the session is updated with the new URL and ids and the upload
    /// continues from byte <c>0</c> against the new direct upload (prior partial bytes on the old URL are not carried over).
    /// </param>
    public (MuxUploadHandle handle, Task<MuxUploadOutcome> uploadTask) ContinuePersistedResumableUploadAsync(
        MuxResumableUploadSession session,
        Func<MuxResumableUploadSession, CancellationToken, Task> persistSessionAsync,
        IProgress<MuxUploadProgress>? progress = null,
        CancellationToken externalToken = default,
        MuxAuthRequestContext? authContextForReauth = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(persistSessionAsync);
        if (string.IsNullOrWhiteSpace(session.PutUri))
            throw new ArgumentException("Session PutUri is required.", nameof(session));
        if (string.IsNullOrWhiteSpace(session.LocalFilePath) || !File.Exists(session.LocalFilePath))
            throw new FileNotFoundException("Session local file not found.", session.LocalFilePath);
        if (session.ChunkSizeBytes <= 0)
            throw new ArgumentException("Session ChunkSizeBytes must be positive.", nameof(session));

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var pauseController = new MuxUploadPauseController();
        var task = ContinuePersistedResumableCoreAsync(
            session,
            persistSessionAsync,
            pauseController,
            progress,
            authContextForReauth,
            cts.Token);
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

            var auth = await _authUrlProvider.GetUploadUrlAsync(authContext, cancellationToken).ConfigureAwait(false);
            var totalBytes = ResolveTotalBytes(sourceStream, explicitLength)
                ?? throw new InvalidOperationException("Resumable upload requires a known content length.");

            return await UploadResumableChunksAsync(
                auth,
                sourceStream,
                disposeStream: false,
                totalBytes,
                startOffset: 0,
                chunkSizeBytes,
                contentType,
                progress,
                pauseController,
                onChunkCompleteAsync: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (disposeStream)
                await sourceStream.DisposeAsync();
        }
    }

    private async Task<MuxUploadOutcome> ContinuePersistedResumableCoreAsync(
        MuxResumableUploadSession session,
        Func<MuxResumableUploadSession, CancellationToken, Task> persistSessionAsync,
        MuxUploadPauseController pauseController,
        IProgress<MuxUploadProgress>? progress,
        MuxAuthRequestContext? authContextForReauth,
        CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(session.LocalFilePath);
        if (!fs.CanSeek)
            throw new InvalidOperationException("Local file stream must be seekable.");

        var totalBytes = fs.Length;
        if (session.FileSizeBytes != totalBytes)
        {
            session.FileSizeBytes = totalBytes;
            session.LastUpdatedUtc = DateTimeOffset.UtcNow;
            await persistSessionAsync(session, cancellationToken).ConfigureAwait(false);
        }

        var probeUsedReauth = false;
        long startOffset;
        while (true)
        {
            var putUri = new Uri(session.PutUri, UriKind.Absolute);
            var probe = await TryProbeMuxResumeOffsetAsync(
                    _httpClient,
                    putUri,
                    totalBytes,
                    session.ContentType,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!probe.Unauthorized)
            {
                startOffset = probe.NextOffset;
                break;
            }

            if (authContextForReauth is null)
            {
                throw new HttpRequestException(
                    "Resume probe unauthorized/forbidden — signed PUT URL may have expired. Pass authContextForReauth to retry with a fresh URL, or create a new persisted session.");
            }

            if (probeUsedReauth)
                throw new HttpRequestException(
                    "Resume probe still unauthorized after re-auth — check backend credentials or token.");

            await RefreshPersistedSessionFromAuthAsync(session, authContextForReauth, persistSessionAsync, cancellationToken)
                .ConfigureAwait(false);
            probeUsedReauth = true;
            startOffset = 0;
            break;
        }

        if (startOffset < 0)
            startOffset = 0;
        if (startOffset > totalBytes)
            startOffset = totalBytes;

        session.BytesUploadedSoFar = startOffset;
        session.LastUpdatedUtc = DateTimeOffset.UtcNow;
        await persistSessionAsync(session, cancellationToken).ConfigureAwait(false);

        var auth = new MuxAuthUrlResult(
            new Uri(session.PutUri, UriKind.Absolute),
            session.UploadId,
            session.AssetId,
            session.PlaybackId);

        async Task OnChunk(long newOffset, CancellationToken ct)
        {
            session.BytesUploadedSoFar = newOffset;
            session.LastUpdatedUtc = DateTimeOffset.UtcNow;
            await persistSessionAsync(session, ct).ConfigureAwait(false);
        }

        return await UploadResumableChunksAsync(
            auth,
            fs,
            disposeStream: false,
            totalBytes,
            startOffset,
            session.ChunkSizeBytes,
            session.ContentType,
            progress,
            pauseController,
            OnChunk,
            cancellationToken,
            session,
            authContextForReauth,
            persistSessionAsync).ConfigureAwait(false);
    }

    private async Task RefreshPersistedSessionFromAuthAsync(
        MuxResumableUploadSession session,
        MuxAuthRequestContext authContext,
        Func<MuxResumableUploadSession, CancellationToken, Task> persistSessionAsync,
        CancellationToken cancellationToken)
    {
        var auth = await _authUrlProvider.GetUploadUrlAsync(authContext, cancellationToken).ConfigureAwait(false);
        session.PutUri = auth.PutUri.AbsoluteUri;
        session.UploadId = auth.UploadId;
        session.AssetId = auth.AssetId;
        session.PlaybackId = auth.PlaybackId;
        session.BytesUploadedSoFar = 0;
        session.LastUpdatedUtc = DateTimeOffset.UtcNow;
        await persistSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes the Mux direct-upload URL for how many bytes are already stored (resume offset).
    /// </summary>
    public static async Task<long> ProbeMuxResumeOffsetAsync(
        HttpClient httpClient,
        Uri putUri,
        long totalBytes,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        var probe = await TryProbeMuxResumeOffsetAsync(httpClient, putUri, totalBytes, contentType, cancellationToken)
            .ConfigureAwait(false);
        if (probe.Unauthorized)
        {
            throw new HttpRequestException(
                "Resume probe unauthorized/forbidden — signed PUT URL may have expired.");
        }

        return probe.NextOffset;
    }

    private static async Task<ProbeMuxResumeResult> TryProbeMuxResumeOffsetAsync(
        HttpClient httpClient,
        Uri putUri,
        long totalBytes,
        string? contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(putUri);
        if (totalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));

        using var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentRange = new ContentRangeHeaderValue(totalBytes);
        if (!string.IsNullOrWhiteSpace(contentType))
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var request = new HttpRequestMessage(HttpMethod.Put, putUri) { Content = content };
        request.Headers.ExpectContinue = false;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            return new ProbeMuxResumeResult(0, Unauthorized: true);
        }

        if (response.StatusCode == HttpStatusCode.OK)
            return new ProbeMuxResumeResult(totalBytes, Unauthorized: false);

        if ((int)response.StatusCode == 308)
        {
            if (TryGetNextOffsetFromHeaders(response, out var next))
                return new ProbeMuxResumeResult(Math.Min(next, totalBytes), Unauthorized: false);
            return new ProbeMuxResumeResult(0, Unauthorized: false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Resume probe failed. Status={(int)response.StatusCode}. Body={body}");
        }

        if (TryGetNextOffsetFromHeaders(response, out var o))
            return new ProbeMuxResumeResult(Math.Min(o, totalBytes), Unauthorized: false);
        return new ProbeMuxResumeResult(0, Unauthorized: false);
    }

    private static bool TryGetNextOffsetFromHeaders(HttpResponseMessage response, out long nextByteOffset)
    {
        nextByteOffset = 0;
        if (response.Headers.TryGetValues("Range", out var rangeVals))
        {
            var v = rangeVals.FirstOrDefault();
            if (TryParseRangeHeaderToNextOffset(v, out nextByteOffset))
                return true;
        }

        if (response.Content.Headers.ContentRange is { } cr)
        {
            if (cr.HasRange && cr.To is not null)
            {
                nextByteOffset = cr.To.Value + 1;
                return true;
            }
        }

        // Some stacks echo Content-Range on the response
        if (response.Headers.TryGetValues("Content-Range", out var crVals))
        {
            var s = crVals.FirstOrDefault();
            if (TryParseContentRangeStringToNextOffset(s, out nextByteOffset))
                return true;
        }

        return false;
    }

    private static bool TryParseRangeHeaderToNextOffset(string? rangeValue, out long nextByteOffset)
    {
        nextByteOffset = 0;
        if (string.IsNullOrWhiteSpace(rangeValue))
            return false;
        // Range: bytes=0-524287
        var eq = rangeValue.IndexOf('=');
        if (eq < 0)
            return false;
        var span = rangeValue.AsSpan(eq + 1).Trim();
        var dash = span.IndexOf('-');
        if (dash < 0)
            return false;
        var endPart = span[(dash + 1)..];
        if (!long.TryParse(endPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endInclusive))
            return false;
        nextByteOffset = endInclusive + 1;
        return true;
    }

    private static bool TryParseContentRangeStringToNextOffset(string? value, out long nextByteOffset)
    {
        nextByteOffset = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        // Content-Range: bytes 0-524287/25000000
        var slash = value.LastIndexOf('/');
        if (slash < 0)
            return false;
        var rangePart = value.AsSpan(0, slash).Trim();
        var space = rangePart.LastIndexOf(' ');
        if (space < 0)
            return false;
        var numbers = rangePart[(space + 1)..].Trim();
        var dash = numbers.IndexOf('-');
        if (dash < 0)
            return false;
        if (!long.TryParse(numbers[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var endInclusive))
            return false;
        nextByteOffset = endInclusive + 1;
        return true;
    }

    private async Task<MuxUploadOutcome> UploadResumableChunksAsync(
        MuxAuthUrlResult auth,
        Stream sourceStream,
        bool disposeStream,
        long totalBytes,
        long startOffset,
        int chunkSizeBytes,
        string? contentType,
        IProgress<MuxUploadProgress>? progress,
        MuxUploadPauseController pauseController,
        Func<long, CancellationToken, Task>? onChunkCompleteAsync,
        CancellationToken cancellationToken,
        MuxResumableUploadSession? sessionForReauth = null,
        MuxAuthRequestContext? reauthContext = null,
        Func<MuxResumableUploadSession, CancellationToken, Task>? persistForReauth = null)
    {
        try
        {
            if (startOffset > totalBytes)
                startOffset = totalBytes;

            progress?.Report(new MuxUploadProgress(startOffset, totalBytes));

            var currentAuth = auth;
            var chunkReauthUsed = false;
            var offset = startOffset;
            while (offset < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await pauseController.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);

                var chunkLength = Math.Min(chunkSizeBytes, totalBytes - offset);
                var end = offset + chunkLength - 1;

                // Do not report progress from inside SerializeToStreamAsync: many HttpClient stacks buffer the
                // request body quickly, so bytes-written callbacks can jump to ~100% while the network upload continues.
                // Progress is reported only after each chunk HTTP response (bytes committed per Mux resumable semantics).
                using var content = new RangedStreamContent(
                    sourceStream,
                    offset,
                    chunkLength,
                    1024 * 1024,
                    onProgress: null,
                    cancellationToken);

                content.Headers.ContentRange = new ContentRangeHeaderValue(offset, end, totalBytes);
                if (!string.IsNullOrWhiteSpace(contentType))
                    content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                using var request = new HttpRequestMessage(HttpMethod.Put, currentAuth.PutUri)
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
                    var canReauthChunk = sessionForReauth is not null
                        && reauthContext is not null
                        && persistForReauth is not null
                        && !chunkReauthUsed;
                    if (!canReauthChunk)
                    {
                        var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                        throw new HttpRequestException(
                            $"Upload unauthorized/forbidden. Status={(int)response.StatusCode}. Body={body}");
                    }

                    chunkReauthUsed = true;
                    var sess = sessionForReauth!;
                    await RefreshPersistedSessionFromAuthAsync(sess, reauthContext!, persistForReauth!, cancellationToken)
                        .ConfigureAwait(false);
                    currentAuth = new MuxAuthUrlResult(
                        new Uri(sess.PutUri, UriKind.Absolute),
                        sess.UploadId,
                        sess.AssetId,
                        sess.PlaybackId);
                    offset = 0;
                    sourceStream.Position = 0;
                    progress?.Report(new MuxUploadProgress(0, totalBytes));
                    continue;
                }

                // Final chunk: 200 OK. Intermediate chunks: 308 Resume Incomplete.
                if (response.StatusCode != HttpStatusCode.OK && (int)response.StatusCode != 308)
                {
                    var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                    throw new HttpRequestException($"Chunk upload failed. Status={(int)response.StatusCode}. Body={body}");
                }

                offset += chunkLength;
                progress?.Report(new MuxUploadProgress(offset, totalBytes));

                if (onChunkCompleteAsync is not null)
                    await onChunkCompleteAsync(offset, cancellationToken).ConfigureAwait(false);
            }

            MuxUploadDetails? details = null;
            if (_uploadDetailsProvider is not null && !string.IsNullOrWhiteSpace(currentAuth.UploadId))
            {
                details = await _uploadDetailsProvider
                    .GetUploadDetailsAsync(currentAuth.UploadId!, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new MuxUploadOutcome(currentAuth, details);
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
            long lastReported = 0;
            const long minReportStepBytes = 512 * 1024;

            while (true)
            {
                _ct.ThrowIfCancellationRequested();

                var read = await _source.ReadAsync(buffer.AsMemory(0, buffer.Length), _ct);
                if (read == 0)
                    break;

                await stream.WriteAsync(buffer.AsMemory(0, read), _ct);
                totalSent += read;
                if (totalSent - lastReported >= minReportStepBytes)
                {
                    _onProgress(totalSent);
                    lastReported = totalSent;
                }
            }

            if (totalSent != lastReported)
                _onProgress(totalSent);
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
        private readonly Action<long>? _onProgress;
        private readonly CancellationToken _ct;

        public RangedStreamContent(
            Stream source,
            long start,
            long length,
            int bufferSize,
            Action<long>? onProgress,
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
                _onProgress?.Invoke(sent);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
