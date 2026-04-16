using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Calls your auth-url endpoint with HTTP Basic authentication on each request.
/// Useful for simple app-level credentials such as a predefined username/password
/// validated by your backend.
/// </summary>
public sealed class BasicAuthMuxAuthUrlProvider : IMuxAuthUrlProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpointPath;
    private readonly Func<CancellationToken, Task<(string Username, string Password)>> _getCredentialsAsync;

    public BasicAuthMuxAuthUrlProvider(
        HttpClient httpClient,
        string endpointPath,
        Func<CancellationToken, Task<(string Username, string Password)>> getCredentialsAsync)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
        _getCredentialsAsync = getCredentialsAsync ?? throw new ArgumentNullException(nameof(getCredentialsAsync));
    }

    public async Task<Uri> GetUploadUrlAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await _getCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
            throw new InvalidOperationException("Basic auth username or password is null or empty.");

        var basic = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}"));

        using var request = new HttpRequestMessage(HttpMethod.Get, _endpointPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

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
