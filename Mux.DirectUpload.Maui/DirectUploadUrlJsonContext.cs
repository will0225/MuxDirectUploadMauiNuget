using System.Text.Json.Serialization;

namespace Mux.DirectUpload.Maui;

internal sealed record DirectUploadUrlResponse(string UploadUrl);

[JsonSerializable(typeof(DirectUploadUrlResponse))]
internal sealed partial class DirectUploadUrlJsonContext : JsonSerializerContext { }

