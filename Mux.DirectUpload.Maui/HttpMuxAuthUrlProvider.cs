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

    public async Task<Uri> GetUploadUrlAsync(CancellationToken cancellationToken = default)
    {
        var resp = await _httpClient.GetFromJsonAsync(
                       _endpointPath,
                       DirectUploadUrlJsonContext.Default.DirectUploadUrlResponse,
                       cancellationToken)
                   ?? throw new InvalidOperationException("Mux upload URL response was null.");

        if (string.IsNullOrWhiteSpace(resp.UploadUrl))
            throw new InvalidOperationException("Mux upload URL response did not contain UploadUrl.");

        return new Uri(resp.UploadUrl, UriKind.Absolute);
    }
}

