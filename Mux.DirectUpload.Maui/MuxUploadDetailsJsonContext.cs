using System.Text.Json.Serialization;

namespace Mux.DirectUpload.Maui;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MuxUploadDetails))]
[JsonSerializable(typeof(MuxUploadNewAssetSettings))]
[JsonSerializable(typeof(MuxPlaybackIdItem))]
[JsonSerializable(typeof(List<MuxPlaybackIdItem>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(MuxUploadError))]
[JsonSerializable(typeof(MuxWebhookStatusSnapshot))]
internal sealed partial class MuxUploadDetailsJsonContext : JsonSerializerContext { }
