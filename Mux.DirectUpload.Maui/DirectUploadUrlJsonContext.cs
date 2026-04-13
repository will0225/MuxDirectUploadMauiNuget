using System.Text.Json.Serialization;

namespace Mux.DirectUpload.Maui;

/// <summary>
/// Auth backends typically return camelCase JSON: <c>{"uploadUrl":"..."}</c>.
/// </summary>
internal sealed record DirectUploadUrlResponse(string UploadUrl);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DirectUploadUrlResponse))]
internal sealed partial class DirectUploadUrlJsonContext : JsonSerializerContext { }

