using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Polls your backend <c>GET</c> URL (e.g. Firebase <c>getMuxWebhookStatus?uploadId=…</c>) until the Mux webhook has written
/// playback info to Firestore or a timeout elapses. Maps the response to <see cref="MuxUploadDetails"/>.
/// </summary>
public sealed class HttpMuxWebhookStatusProvider : IMuxUploadDetailsProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpointPathFormat;
    private readonly Func<CancellationToken, Task<string>>? _getBearerTokenAsync;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxWait;

    /// <param name="endpointPathFormat">Relative path with exactly one <c>{0}</c> placeholder for the URL-encoded upload id.</param>
    /// <param name="getBearerTokenAsync">If set, sends <c>Authorization: Bearer …</c> on each poll request.</param>
    /// <param name="pollInterval">Delay between attempts while the webhook has not produced a “ready” snapshot.</param>
    /// <param name="maxWait">Give up after this duration and return the last snapshot received, if any.</param>
    public HttpMuxWebhookStatusProvider(
        HttpClient httpClient,
        string endpointPathFormat,
        Func<CancellationToken, Task<string>>? getBearerTokenAsync = null,
        TimeSpan? pollInterval = null,
        TimeSpan? maxWait = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpointPathFormat = endpointPathFormat ?? throw new ArgumentNullException(nameof(endpointPathFormat));
        if (!_endpointPathFormat.Contains("{0}", StringComparison.Ordinal))
            throw new ArgumentException("endpointPathFormat must contain {0} for the upload id.", nameof(endpointPathFormat));
        _getBearerTokenAsync = getBearerTokenAsync;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _maxWait = maxWait ?? TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    public async Task<MuxUploadDetails?> GetUploadDetailsAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
            return null;

        var id = uploadId.Trim();
        var deadline = DateTime.UtcNow + _maxWait;
        MuxUploadDetails? last = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snap = await TryGetSnapshotAsync(id, cancellationToken).ConfigureAwait(false);
            if (snap is not null)
            {
                last = MapToUploadDetails(snap, id);
                if (IsReady(last))
                    return last;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var delay = _pollInterval;
            if (DateTime.UtcNow + delay > deadline)
                delay = TimeSpan.FromTicks(Math.Max(0, (deadline - DateTime.UtcNow).Ticks));
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return last;
    }

    private async Task<MuxWebhookStatusSnapshot?> TryGetSnapshotAsync(string uploadId, CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, _endpointPathFormat, Uri.EscapeDataString(uploadId));
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (_getBearerTokenAsync is not null)
        {
            var token = await _getBearerTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Bearer token is null or empty.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(
                MuxUploadDetailsJsonContext.Default.MuxWebhookStatusSnapshot,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static MuxUploadDetails MapToUploadDetails(MuxWebhookStatusSnapshot w, string fallbackUploadId) =>
        new()
        {
            Id = w.UploadId ?? fallbackUploadId,
            Status = null,
            AssetId = w.AssetId,
            PlaybackId = w.PlaybackId,
            PlaybackIds = w.PlaybackIds,
            AssetStatus = w.AssetStatus,
            LastEventType = w.LastEventType,
            NewAssetSettings = string.IsNullOrEmpty(w.Passthrough) ?
                null :
                new MuxUploadNewAssetSettings { Passthrough = w.Passthrough },
        };

    private static bool IsReady(MuxUploadDetails d)
    {
        if (!string.IsNullOrEmpty(d.PlaybackId))
            return true;
        if (string.Equals(d.AssetStatus, "ready", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(d.LastEventType) &&
            d.LastEventType.Contains("asset.ready", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
