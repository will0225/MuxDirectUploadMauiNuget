using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Optional metadata for your auth-url request. Implementations send values as
/// <see cref="HeaderCreatorId">custom headers</see> (preferred) and also append
/// the same fields as query parameters for backends that only read the URL.
/// </summary>
public sealed class MuxAuthRequestContext
{
    public const string HeaderCreatorId = "Mux-Auth-Creator-Id";
    public const string HeaderExternalId = "Mux-Auth-External-Id";
    public const string HeaderAssetMetadata = "Mux-Auth-Asset-Metadata";
    public const string HeaderUploadQualitySettings = "Mux-Auth-Upload-Quality-Settings";

    public string? CreatorId { get; init; }

    public string? ExternalId { get; init; }

    /// <summary>
    /// Key-value pairs merged into Mux asset <c>meta</c> by your backend. Use the key <c>passthrough</c> to set Mux
    /// <c>new_asset_settings.passthrough</c> (single string, max 255 characters); other keys are stored in <c>meta</c>.
    /// Sent as Base64-encoded JSON (UTF-8).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Optional JSON merged into Mux <c>new_asset_settings</c> on your backend (e.g.
    /// <c>encoding_tier</c>, <c>max_resolution_tier</c>, <c>video_quality</c>, <c>mp4_support</c>).
    /// Sent as Base64-encoded JSON in <see cref="HeaderUploadQualitySettings"/> (avoid very large payloads; header size limits vary).
    /// </summary>
    public JsonObject? UploadQualitySettings { get; init; }

    internal static string AppendToEndpointPath(string endpointPath, MuxAuthRequestContext? p)
    {
        if (p is null)
            return endpointPath;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.CreatorId))
            parts.Add("creatorId=" + Uri.EscapeDataString(p.CreatorId));
        if (!string.IsNullOrWhiteSpace(p.ExternalId))
            parts.Add("externalId=" + Uri.EscapeDataString(p.ExternalId));

        var metadataB64 = SerializeMetadataBase64(p);
        if (metadataB64 is not null)
            parts.Add("assetMetadata=" + Uri.EscapeDataString(metadataB64));

        var qualityB64 = SerializeUploadQualitySettingsBase64(p);
        if (qualityB64 is not null)
            parts.Add("uploadQualitySettings=" + Uri.EscapeDataString(qualityB64));

        if (parts.Count == 0)
            return endpointPath;

        var q = string.Join("&", parts);
        return endpointPath.Contains('?', StringComparison.Ordinal)
            ? endpointPath + "&" + q
            : endpointPath + "?" + q;
    }

    internal static void ApplyHeaders(HttpRequestMessage request, MuxAuthRequestContext? context)
    {
        if (context is null)
            return;

        if (!string.IsNullOrWhiteSpace(context.CreatorId))
            request.Headers.TryAddWithoutValidation(HeaderCreatorId, context.CreatorId);
        if (!string.IsNullOrWhiteSpace(context.ExternalId))
            request.Headers.TryAddWithoutValidation(HeaderExternalId, context.ExternalId);

        var metadataB64 = SerializeMetadataBase64(context);
        if (metadataB64 is not null)
            request.Headers.TryAddWithoutValidation(HeaderAssetMetadata, metadataB64);

        var qualityB64 = SerializeUploadQualitySettingsBase64(context);
        if (qualityB64 is not null)
            request.Headers.TryAddWithoutValidation(HeaderUploadQualitySettings, qualityB64);
    }

    internal static string? SerializeUploadQualitySettingsBase64(MuxAuthRequestContext? p)
    {
        if (p?.UploadQualitySettings is null || p.UploadQualitySettings.Count == 0)
            return null;

        var json = p.UploadQualitySettings.ToJsonString();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    internal static string? SerializeMetadataBase64(MuxAuthRequestContext? p)
    {
        if (p?.Metadata is null || p.Metadata.Count == 0)
            return null;

        var json = JsonSerializer.Serialize(p.Metadata);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    internal static string AppendQueryParameter(string endpointPath, string name, string value)
    {
        var segment = $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
        return endpointPath.Contains('?', StringComparison.Ordinal)
            ? endpointPath + "&" + segment
            : endpointPath + "?" + segment;
    }
}
