using System.Net.Http.Json;

namespace Mux.DirectUpload.Maui;

public sealed class HttpMuxAuthUrlProvider : IMuxAuthUrlProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpointPath;

    public HttpMuxAuthUrlProvider(HttpClient httpClient, string endpointPath = "/api/mux/direct-upload-url")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
    }

    public async Task<MuxAuthUrlResult> GetUploadUrlAsync(
        MuxAuthRequestContext? authContext = null,
        CancellationToken cancellationToken = default)
    {
        var path = MuxAuthRequestContext.AppendToEndpointPath(_endpointPath, authContext);
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

