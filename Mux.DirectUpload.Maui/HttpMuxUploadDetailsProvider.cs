using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// GETs your backend URL built from <paramref name="endpointPathFormat"/> with the upload id at <c>{0}</c>
/// (e.g. <c>/getMuxUploadStatus?uploadId={0}</c>). Optional Bearer token per request (e.g. Firebase ID token).
/// </summary>
public sealed class HttpMuxUploadDetailsProvider : IMuxUploadDetailsProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpointPathFormat;
    private readonly Func<CancellationToken, Task<string>>? _getBearerTokenAsync;

    /// <param name="endpointPathFormat">Relative path with exactly one <c>{0}</c> placeholder for the URL-encoded upload id.</param>
    /// <param name="getBearerTokenAsync">If set, sends <c>Authorization: Bearer …</c> (same pattern as <see cref="BearerMuxAuthUrlProvider"/>).</param>
    public HttpMuxUploadDetailsProvider(
        HttpClient httpClient,
        string endpointPathFormat,
        Func<CancellationToken, Task<string>>? getBearerTokenAsync = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpointPathFormat = endpointPathFormat ?? throw new ArgumentNullException(nameof(endpointPathFormat));
        if (!_endpointPathFormat.Contains("{0}", StringComparison.Ordinal))
            throw new ArgumentException("endpointPathFormat must contain {0} for the upload id.", nameof(endpointPathFormat));
        _getBearerTokenAsync = getBearerTokenAsync;
    }

    public async Task<MuxUploadDetails?> GetUploadDetailsAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
            return null;

        var path = string.Format(CultureInfo.InvariantCulture, _endpointPathFormat, Uri.EscapeDataString(uploadId.Trim()));
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
                MuxUploadDetailsJsonContext.Default.MuxUploadDetails,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
