# Mux.DirectUpload.Maui

Direct upload helper for Mux Video from .NET 10 (`net10.0-android`, `net10.0-ios`).

## How it works

1. Your app asks your **backend** for an authenticated Mux Direct Upload **PUT** URL.
2. The library performs an HTTP `PUT` of the video file bytes to that URL and reports progress.

Your backend must create the Direct Upload using the Mux Video API and return the `url` field to the app.

## Minimal usage

```csharp
using Mux.DirectUpload.Maui;

// register services (example)
services.AddHttpClient<IMuxAuthUrlProvider, HttpMuxAuthUrlProvider>(c =>
{
  c.BaseAddress = new Uri("https://your-backend.example.com");
});
services.AddHttpClient<MuxDirectUploader>();

// start an upload
var uploader = serviceProvider.GetRequiredService<MuxDirectUploader>();
var (handle, task) = uploader.StartUploadAsync(
  filePath: "/path/to/video.mp4",
  progress: new Progress<MuxUploadProgress>(p => Console.WriteLine(p.Percent))
);
await task;
```

