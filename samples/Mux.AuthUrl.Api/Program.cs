using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("mux", c =>
{
    c.BaseAddress = new Uri("https://api.mux.com");
});

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "Mux Auth URL API", status = "ok" }));

app.MapGet("/api/mux/direct-upload-url", async (IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    var tokenId = configuration["Mux:TokenId"];
    var tokenSecret = configuration["Mux:TokenSecret"];

    if (string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(tokenSecret))
    {
        return Results.Problem(
            detail: "Mux credentials are missing. Set Mux:TokenId and Mux:TokenSecret.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    var muxClient = httpClientFactory.CreateClient("mux");
    var basicAuthRaw = $"{tokenId}:{tokenSecret}";
    var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(basicAuthRaw));
    muxClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

    var payload = new
    {
        cors_origin = "*",
        timeout = "3600s",
        new_asset_settings = new
        {
            playback_policy = new[] { "public" }
        }
    };

    using var response = await muxClient.PostAsJsonAsync("/video/v1/uploads", payload);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem(
            detail: $"Mux API error {(int)response.StatusCode}: {responseBody}",
            statusCode: StatusCodes.Status502BadGateway);
    }

    using var json = JsonDocument.Parse(responseBody);
    var uploadUrl = json.RootElement.GetProperty("data").GetProperty("url").GetString();

    if (string.IsNullOrWhiteSpace(uploadUrl))
    {
        return Results.Problem(
            detail: "Mux response did not include data.url.",
            statusCode: StatusCodes.Status502BadGateway);
    }

    return Results.Ok(new { uploadUrl });
});

app.Run();
