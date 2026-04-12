using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Calls your auth-url endpoint with <c>Authorization: Bearer …</c> on each request.
/// Use <see cref="Func{TResult}"/> to supply a Firebase ID token (e.g. <c>GetIdTokenAsync</c>) so tokens can be refreshed.
/// </summary>
/// <remarks>
/// Prefer this over setting <see cref="HttpClient.DefaultRequestHeaders"/> on the same <see cref="HttpClient"/>
/// used by <see cref="MuxDirectUploader"/>: default headers would also be sent on the PUT to Mux storage and can break uploads or leak the token.
/// </remarks>
public sealed class BearerMuxAuthUrlProvider : IMuxAuthUrlProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpointPath;
    private readonly Func<CancellationToken, Task<string>> _getBearerTokenAsync;

    /// <param name="httpClient">HTTP client (typically with <see cref="HttpClient.BaseAddress"/> set; do not set a global Bearer header on it).</param>
    /// <param name="endpointPath">Relative path (e.g. <c>/getMuxDirectUploadUrl</c> or <c>/</c>) or absolute URI.</param>
    /// <param name="getBearerTokenAsync">Returns the Firebase ID token (or any Bearer secret) for this request.</param>
    public BearerMuxAuthUrlProvider(
        HttpClient httpClient,
        string endpointPath,
        Func<CancellationToken, Task<string>> getBearerTokenAsync)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
        _getBearerTokenAsync = getBearerTokenAsync ?? throw new ArgumentNullException(nameof(getBearerTokenAsync));
    }

    public async Task<Uri> GetUploadUrlAsync(CancellationToken cancellationToken = default)
    {
        var token = await _getBearerTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Bearer token is null or empty.");

        using var request = new HttpRequestMessage(HttpMethod.Get, _endpointPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var resp = await response.Content.ReadFromJsonAsync(
                DirectUploadUrlJsonContext.Default.DirectUploadUrlResponse,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mux upload URL response was null.");

        if (string.IsNullOrWhiteSpace(resp.UploadUrl))
            throw new InvalidOperationException("Mux upload URL response did not contain UploadUrl.");

        return new Uri(resp.UploadUrl, UriKind.Absolute);
    }
}
