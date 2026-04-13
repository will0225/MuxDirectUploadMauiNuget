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

### Upload from a `Stream` (e.g. MAUI MediaPicker)

When you cannot rely on a file path (e.g. `MediaPicker` / `OpenReadAsync()`), use the overload that accepts a `Stream`. The stream is disposed after the upload unless you pass `leaveOpen: true`.

```csharp
var stream = await fileResult.OpenReadAsync();
var (handle, task) = uploader.StartUploadAsync(
    stream,
    contentLength: stream.CanSeek ? null : knownLengthIfAvailable,
    contentType: "video/mp4",
    leaveOpen: false,
    progress: progress);
await task;
```

If the stream is not seekable and you cannot supply `contentLength`, progress may not show a total size and the PUT may use chunked encoding (depending on runtime); some storage backends require a known length.

### Large files / long uploads

`HttpClient` often defaults to a **100 second** total request timeout. Big uploads can exceed that and fail mid-stream with `IOException` on the transport. Set a longer or infinite timeout on the `HttpClient` you pass to `MuxDirectUploader`, e.g. `httpClient.Timeout = TimeSpan.FromMilliseconds(-1)` (infinite) or `TimeSpan.FromHours(2)`.

## Firebase auth URL (Bearer token)

The package does **not** embed Firebase SDKs. Your app signs in with Firebase Auth and supplies the **ID token**.

Use **`BearerMuxAuthUrlProvider`**: it adds `Authorization: Bearer` only to the auth-url **GET**, not to the Mux **PUT** (avoid putting `DefaultRequestHeaders.Authorization` on the same `HttpClient` used for upload).

```csharp
var http = new HttpClient { BaseAddress = new Uri("https://us-central1-PROJECT.cloudfunctions.net") };
var auth = new BearerMuxAuthUrlProvider(
    http,
    endpointPath: "/getMuxDirectUploadUrl", // or "/" for some Cloud Run URLs
    getBearerTokenAsync: async ct => await firebaseAuthUser.GetIdTokenAsync());

var uploader = new MuxDirectUploader(http, auth);
```

Replace `GetIdTokenAsync` with your Firebase user API. For **token refresh**, return a fresh token each call (Firebase SDK usually handles that when you call `GetIdTokenAsync(true)` if needed).

You **cannot** “pass Firebase automatically” from the Cloud Function alone: the app must identify the user; the function only verifies the JWT. Alternatives (API keys in the app, unauthenticated endpoints) are weaker and not recommended.

