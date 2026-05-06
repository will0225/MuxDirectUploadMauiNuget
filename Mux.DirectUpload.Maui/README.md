# Mux.DirectUpload.Maui

Direct upload helper for Mux Video from .NET 10 (`net10.0-android`, `net10.0-ios`).

## How it works

1. Your app asks your **backend** for an authenticated Mux Direct Upload **PUT** URL.
2. The library performs an HTTP `PUT` of the video file bytes to that URL and reports progress.

Your backend must create the Direct Upload using the Mux Video API and return JSON with at least **`uploadUrl`**. Optionally include **`uploadId`**, **`assetId`**, and **`playbackId`** (camelCase) so the app can use them after upload; the library maps them to **`MuxAuthUrlResult`**.

**v3.0:** `IMuxAuthUrlProvider.GetUploadUrlAsync` returns **`Task<MuxAuthUrlResult>`** (not `Uri`). **v4.0:** `StartUploadAsync` completes with **`Task<MuxUploadOutcome>`** (wrapped **`Auth`** + optional **`UploadDetails`**), not `MuxAuthUrlResult` directly—use **`outcome.Auth`** for the PUT URL and ids.

**After the PUT:** To mirror `GET https://api.mux.com/video/v1/uploads/{upload_id}` safely, **do not call Mux from the app with API tokens**. Implement **`IMuxUploadDetailsProvider`** (e.g. **`HttpMuxUploadDetailsProvider`**) pointing at your backend; the sample Firebase function **`getMuxUploadStatus`** proxies that call and **merges the Asset** (`GET /video/v1/assets/{asset_id}`) so **`playbackId`**, **`playback_ids`**, **`asset_status`**, and **`asset_meta`** are included—**playback IDs are not on the upload object** in Mux’s API. The upload JSON also echoes **`new_asset_settings`** (including **`meta`** and **`passthrough`** you set at create time); **`MuxUploadDetails`** maps those. If **`playbackId`** is still empty, the asset may not be **ready** yet—retry after a few seconds or use webhooks.

**Playback id timing:** Mux usually sets **`asset_id`** (and thus playback ids) **after** the file is uploaded and processed. The sample create-upload function returns **`playbackId`** only when **`asset_id`** is already present; often **`playbackId` is null** in the first response—poll **`UploadDetails`** or use webhooks.

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
var outcome = await task;
// outcome.Auth.PutUri, outcome.Auth.UploadId, outcome.UploadDetails?.Status, outcome.UploadDetails?.AssetId
```

Optional: pass **`IMuxUploadDetailsProvider`** so **`UploadDetails`** is filled after the PUT succeeds:

```csharp
var details = new HttpMuxUploadDetailsProvider(
    http,
    endpointPathFormat: "/getMuxUploadStatus?uploadId={0}",
    getBearerTokenAsync: async ct => await user.GetIdTokenAsync());
var uploader = new MuxDirectUploader(http, auth, details);
```

If your backend stores Mux webhook results in Firestore (or similar) and exposes **`getMuxWebhookStatus`**, use **`HttpMuxWebhookStatusProvider`** instead: it polls until **`playbackId`** / **`video.asset.ready`**-style data appears (or a timeout), so the client does not hammer Mux’s upload API.

```csharp
var webhookDetails = new HttpMuxWebhookStatusProvider(
    http,
    endpointPathFormat: "/getMuxWebhookStatus?uploadId={0}",
    getBearerTokenAsync: async ct => await user.GetIdTokenAsync());
var uploader = new MuxDirectUploader(http, auth, webhookDetails);
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

### Pause / resume large uploads

For large files, use the opt-in resumable upload path. This sends Mux direct-upload chunks with `Content-Range`, so `Pause()` waits for the current chunk to finish and `Resume()` continues with the next chunk. `Cancel()` aborts the in-flight request.

**Progress:** For resumable uploads, **`IProgress<MuxUploadProgress>`** advances **once per completed HTTP chunk** (after Mux acknowledges that chunk), not continuously within a chunk. That matches bytes reliably uploaded and avoids the UI jumping to ~100% while `HttpClient` is still draining the socket—many stacks buffer request data ahead of the wire.

```csharp
var (handle, task) = uploader.StartResumableUploadAsync(
    filePath: "/path/to/video.mp4",
    chunkSizeBytes: 8 * 1024 * 1024,
    contentType: "video/mp4",
    progress: progress,
    authContext: authContext);

handle.Pause();  // pauses after current chunk
handle.Resume(); // continues with next chunk
handle.Cancel(); // aborts the upload

var outcome = await task;
```

The stream overload for resumable uploads requires a seekable stream and known length; use the file-path overload when possible.

### Persisted resumable uploads (survive app restart)

In-process pause/resume only helps while the app is running. To resume after a crash, OS kill, or user force-quit, persist **`MuxResumableUploadSession`** (e.g. JSON in app data) and call **`ContinuePersistedResumableUploadAsync`**. The uploader sends a **probe** `PUT` with an empty body and `Content-Range: bytes */<total>`; Mux responds with **308** and a **`Range`** header (or **200** when the file is already complete) so the next byte offset is authoritative. After each successful chunk, save the session again (the library passes a **`persistSessionAsync`** callback for that).

**Caveats:** The signed **PUT URL can expire**. Pass **`authContextForReauth`** (same shape as `CreatePersistedUploadSessionAsync`) into **`ContinuePersistedResumableUploadAsync`**: on **401/403** during probe or a chunk, the library requests a **fresh** direct upload once, updates **`PutUri`** / ids on the session, resets progress to byte **0** for that new Mux upload, persists, and continues (bytes previously accepted under the old URL are not merged — same as a new direct upload). Without **`authContextForReauth`**, **401/403** surfaces as **`HttpRequestException`**.

**Byte source on resume:** Either keep a stable path in **`LocalFilePath`** (copy gallery/picker files into **app data** first when temp paths may disappear), **or** leave **`LocalFilePath`** empty and call **`ContinuePersistedResumableUploadAsync`** with a **`Func<CancellationToken, Task<Stream>>`** that opens the same bytes again (seekable stream). You can build the persisted session from a file path (**`CreatePersistedUploadSessionAsync(string)`**) or from a seekable stream (**`CreatePersistedUploadSessionAsync(Stream, persistedLocalPathForSession: ...)`** — optional path is stored for file-based continuation after restart).

The demo copies into **`AppDataDirectory/mux_uploads`** when the pick has no reliable **`FullPath`**, then creates the persisted session from a **`FileStream`** so behavior does not depend on **`FileResult.FullPath`**. Reuse the **same** `ChunkSizeBytes` and `ContentType` you used when the session was created.

The demo app persists session rows in **SQLite** (`MuxUploadSqliteSessionStore`, database under app data); it migrates a legacy **`mux_resumable_session.json`** file on first launch if present.

```csharp
// New upload: get auth once, save session, then upload (persists offset after each chunk)
var session = await uploader.CreatePersistedUploadSessionAsync(
    filePath,
    contentType: "video/mp4",
    authContext: authContext);
await File.WriteAllTextAsync(sessionPath, JsonSerializer.Serialize(session), ct);

var (handle, task) = uploader.ContinuePersistedResumableUploadAsync(
    session,
    async (s, ct) =>
    {
        await File.WriteAllTextAsync(sessionPath, JsonSerializer.Serialize(s), ct);
    },
    progress: progress,
    authContextForReauth: authContext);
var outcome = await task;
File.Delete(sessionPath); // on success

// After restart: deserialize session from disk, same ContinuePersistedResumableUploadAsync

// Path-free continuation (e.g. resolve Android content URI or your own key each launch):
// var (handle2, task2) = uploader.ContinuePersistedResumableUploadAsync(
//     session,
//     openSeekableStreamAsync: async ct => await OpenSeekableVideoAsync(session, ct),
//     persistSessionAsync: ...,
//     leaveOpen: false,
//     progress: progress,
//     authContextForReauth: authContext);
```

For testing or custom clients, **`MuxDirectUploader.ProbeMuxResumeOffsetAsync(HttpClient, Uri, long totalBytes, string? contentType, CancellationToken)`** returns the next byte offset without uploading data.

### Large files / long uploads

`HttpClient` often defaults to a **100 second** total request timeout. Big uploads can exceed that and fail mid-stream with `IOException` on the transport. Set a longer or infinite timeout on the `HttpClient` you pass to `MuxDirectUploader`, e.g. `httpClient.Timeout = TimeSpan.FromMilliseconds(-1)` (infinite) or `TimeSpan.FromHours(2)`.

## Auth URL options

The package does not require Firebase specifically. Your app only needs an auth-url provider that can call your backend and return `{ "uploadUrl": "..." }`.

### Basic auth username/password

Use **`BasicAuthMuxAuthUrlProvider`** when your backend expects a predefined username/password via HTTP Basic auth.

```csharp
var http = new HttpClient { BaseAddress = new Uri("https://your-auth-url.example.com") };
var auth = new BasicAuthMuxAuthUrlProvider(
    http,
    endpointPath: "/getMuxDirectUploadUrl",
    getCredentialsAsync: ct => Task.FromResult(("your-username", "your-password")));

var uploader = new MuxDirectUploader(http, auth);
```

### Firebase Bearer token

If your backend uses Firebase Auth, use **`BearerMuxAuthUrlProvider`**. Your app signs in with Firebase Auth and supplies the ID token.

```csharp
var http = new HttpClient { BaseAddress = new Uri("https://us-central1-PROJECT.cloudfunctions.net") };
var auth = new BearerMuxAuthUrlProvider(
    http,
    endpointPath: "/getMuxDirectUploadUrl", // or "/" for some Cloud Run URLs
    getBearerTokenAsync: async ct => await firebaseAuthUser.GetIdTokenAsync());

var uploader = new MuxDirectUploader(http, auth);
```

Replace `GetIdTokenAsync` with your Firebase user API. For **token refresh**, return a fresh token each call (Firebase SDK usually handles that when you call `GetIdTokenAsync(true)` if needed).

### Firebase ID token in the query string (no Bearer header)

If your Cloud Function expects the ID token as a query parameter (e.g. `?token=…`) instead of `Authorization: Bearer`, use **`QueryParamFirebaseTokenMuxAuthUrlProvider`**. The default parameter name is `token`; pass another name as the fourth constructor argument if your function uses something else.

```csharp
var auth = new QueryParamFirebaseTokenMuxAuthUrlProvider(
    http,
    endpointPath: "/getMuxDirectUploadUrl",
    getFirebaseIdTokenAsync: async ct => await firebaseAuthUser.GetIdTokenAsync(),
    tokenQueryParameterName: "token");
```

Your function should read `req.query.token` (or your chosen name) and call `verifyIdToken`. Query strings can show up in logs; prefer Bearer when you can.

### Upload metadata (creator / external / asset metadata)

Pass optional `MuxAuthRequestContext` on `StartUploadAsync` (after `CancellationToken`). The library sends creator/external as **custom headers** (`Mux-Auth-Creator-Id`, `Mux-Auth-External-Id`) and query params `creatorId`, `externalId`. **`Metadata`** is a `Dictionary` of string key-value pairs: serialized to JSON, Base64-encoded, and sent as header `Mux-Auth-Asset-Metadata` and query `assetMetadata`. Your backend should merge `Metadata` into Mux `new_asset_settings.meta`. Use the key **`passthrough`** inside `Metadata` to set Mux’s single-string `passthrough` field (max **255** characters); other keys are normal `meta` entries.

Optional **`UploadQualitySettings`** (`System.Text.Json.Nodes.JsonObject`) is merged into Mux `new_asset_settings` by your backend. It is sent as Base64 JSON in the `Mux-Auth-Upload-Quality-Settings` header and as the `uploadQualitySettings` query parameter (same payload). Use Mux **Create Direct Upload** / asset field names (snake_case), e.g. `encoding_tier`, `max_resolution_tier`, `video_quality` (see [Mux API](https://docs.mux.com/api-reference)).

```csharp
using System.Text.Json.Nodes;

var (handle, task) = uploader.StartUploadAsync(
    stream,
    contentType: "video/mp4",
    leaveOpen: false,
    progress: progress,
    externalToken: CancellationToken.None,
    authContext: new MuxAuthRequestContext
    {
        CreatorId = "creator-123",
        ExternalId = "ext-456",
        Metadata = new Dictionary<string, string>
        {
            ["passthrough"] = firebaseUid,
            ["app_tenant"] = "tenant-1",
        },
        UploadQualitySettings = new JsonObject
        {
            ["encoding_tier"] = "smart",
            ["max_resolution_tier"] = "1080p",
        },
    });
```

