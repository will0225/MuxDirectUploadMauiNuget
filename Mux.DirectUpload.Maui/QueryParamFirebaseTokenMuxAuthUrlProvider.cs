using System.Net.Http.Json;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Calls your auth-url endpoint with the Firebase ID token in the query string (no <c>Authorization: Bearer</c> header).
/// Use when your backend or Cloud Function expects <c>?token=…</c> (or a custom name) instead of a Bearer token.
/// </summary>
/// <remarks>
/// Query strings can appear in access logs and referrer headers; prefer <see cref="BearerMuxAuthUrlProvider"/> when possible.
/// </remarks>
public sealed class QueryParamFirebaseTokenMuxAuthUrlProvider : IMuxAuthUrlProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpointPath;
    private readonly string _tokenQueryParameterName;
    private readonly Func<CancellationToken, Task<string>> _getFirebaseIdTokenAsync;

    /// <param name="httpClient">HTTP client (typically with <see cref="HttpClient.BaseAddress"/> set).</param>
    /// <param name="endpointPath">Relative path or absolute URI.</param>
    /// <param name="getFirebaseIdTokenAsync">Returns the Firebase ID token for this request.</param>
    /// <param name="tokenQueryParameterName">Query parameter name (default <c>token</c>).</param>
    public QueryParamFirebaseTokenMuxAuthUrlProvider(
        HttpClient httpClient,
        string endpointPath,
        Func<CancellationToken, Task<string>> getFirebaseIdTokenAsync,
        string tokenQueryParameterName = "token")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
        _getFirebaseIdTokenAsync = getFirebaseIdTokenAsync ?? throw new ArgumentNullException(nameof(getFirebaseIdTokenAsync));
        _tokenQueryParameterName = string.IsNullOrWhiteSpace(tokenQueryParameterName)
            ? throw new ArgumentException("Token query parameter name is required.", nameof(tokenQueryParameterName))
            : tokenQueryParameterName.Trim();
    }

    public async Task<MuxAuthUrlResult> GetUploadUrlAsync(
        MuxAuthRequestContext? authContext = null,
        CancellationToken cancellationToken = default)
    {
        var token = await _getFirebaseIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Firebase ID token is null or empty.");

        var path = MuxAuthRequestContext.AppendToEndpointPath(_endpointPath, authContext);
        path = MuxAuthRequestContext.AppendQueryParameter(path, _tokenQueryParameterName, token.Trim());

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        MuxAuthRequestContext.ApplyHeaders(request, authContext);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var resp = await response.Content.ReadFromJsonAsync(
                DirectUploadUrlJsonContext.Default.DirectUploadUrlResponse,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mux upload URL response was null.");

        return DirectUploadUrlResponseMapper.ToMuxAuthUrlResult(resp);
    }
}
