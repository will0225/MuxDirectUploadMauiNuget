# Mux.DirectUpload.Maui

.NET 10 package and demo for uploading videos directly from MAUI apps to Mux using Direct Upload URLs.

## License

This repository is licensed under **Apache-2.0**, matching the Mux iOS and Android Upload SDK license.

## Projects

- `Mux.DirectUpload.Maui` - reusable library/NuGet package.
- `Mux.DirectUpload.Demo` - .NET 10 MAUI demo app (Android + iOS + Windows).
- `samples/Mux.AuthUrl.Api` - sample backend API that creates Direct Upload URLs via Mux API.
- `samples/Firebase.MuxAuthUrl.Functions` - Firebase Functions (Node.js) sample auth endpoint for Direct Upload URLs.
- **Shared Firebase test URL** (no backend required for quick tests): `https://getmuxdirectuploadurl-fy5arhfiaq-uc.a.run.app` — details in [Shared test auth URL](#shared-test-auth-url-firebase) below.

## Build

```bash
dotnet build -c Release Mux.DirectUpload.Maui/Mux.DirectUpload.Maui.csproj
dotnet pack -c Release Mux.DirectUpload.Maui/Mux.DirectUpload.Maui.csproj -o artifacts
# The NuGet package is created only by `dotnet pack` (not plain `dotnet build`). Output: `artifacts/Mux.DirectUpload.Maui.1.0.0.nupkg` (version from the csproj).
dotnet build -c Release Mux.DirectUpload.Demo/Mux.DirectUpload.Demo.csproj -f net10.0-windows10.0.19041.0
dotnet build -c Release samples/Mux.AuthUrl.Api/Mux.AuthUrl.Api.csproj
```

On **macOS**, build the demo for iOS or Mac Catalyst instead of Windows, for example:

```bash
dotnet build -c Debug Mux.DirectUpload.Demo/Mux.DirectUpload.Demo.csproj -f net10.0-ios
dotnet build -c Debug Mux.DirectUpload.Demo/Mux.DirectUpload.Demo.csproj -f net10.0-maccatalyst
```

## Test on Mac (iOS / Mac Catalyst)

1. **Machine:** Apple Silicon or Intel Mac with enough disk space for Xcode.
2. **Install:** [Xcode](https://developer.apple.com/xcode/) from the App Store (includes iOS Simulator). Open Xcode once and accept the license; install a simulator runtime if prompted.
3. **Install .NET 10 SDK** (same major version as the repo) from [Microsoft’s download page](https://dotnet.microsoft.com/download).
4. **Install MAUI workload:**

   ```bash
   dotnet workload install maui
   ```

5. **Clone or copy** this repository onto the Mac and open `MuxDirectUploadMauiNuget.slnx` in **Visual Studio Code** or **Cursor** (C# / .NET MAUI extensions) or **JetBrains Rider**, or build from the terminal with the commands above.
6. **Run:** Set the startup project to `Mux.DirectUpload.Demo` and pick an **iOS Simulator** or a **connected iPhone** (with signing configured in the project). For Mac desktop testing, choose **Mac Catalyst** as the target.
7. **Backend:** Run your auth URL API (ASP.NET sample, Firebase function, or other host) on a URL reachable from the simulator/device (use your LAN IP or HTTPS tunnel, not only `localhost`, if the device cannot reach your PC’s localhost).

### iOS device: MT1006 and “The input string '1 (a)' was not in a correct format”

This usually comes from **Apple’s iOS/iPadOS build labels** like `26.3.1 (a)` (security update), which older **mlaunch** tooling misparses—**not** from your app version in the csproj. Typical fixes:

- **Update the device** to a newer iOS/iPadOS (e.g. **26.4+**) if available; many users report that resolves deploy.
- **Use the iOS Simulator** on the Mac until your .NET iOS workload includes the fix.
- **Rename the device** in Settings → General → About if the name contains **apostrophes** or odd characters (another reported parser failure).
- Track upstream: [dotnet/maui#34555](https://github.com/dotnet/maui/issues/34555), [dotnet/macios#24935](https://github.com/dotnet/macios/issues/24935).

## Shared test auth URL (Firebase)

A deployed sample Firebase function is available so you can try the flow without hosting your own API:

- **Base URL:** `https://getmuxdirectuploadurl-fy5arhfiaq-uc.a.run.app`
- **Method:** `GET` against the service root (use `/` as the path when configuring `HttpClient`).
- **Auth:** send a valid Firebase **ID token** in `Authorization: Bearer <token>` (see `samples/Firebase.MuxAuthUrl.Functions/README.md`).

In the MAUI demo, set the HTTP client **base address** to the URL above (no trailing slash required) and the auth-url **path** to `/`. Ensure the app attaches the Bearer token to requests (the packaged `HttpMuxAuthUrlProvider` only calls the URL; it does not sign in to Firebase for you).

This endpoint is offered for **testing and demos** only; it may be rate-limited, changed, or taken down. Production apps should deploy and control their own backend.

## Demo app usage

1. Start your auth-url backend API (or use the shared test URL above).
2. In the app, set:
   - Backend base URL, e.g. `https://your-api.example.com` or the shared test URL
   - Endpoint path, e.g. `/api/mux/direct-upload-url`, or `/` for the shared Firebase test URL
3. Pick a local video file.
4. Start upload and observe progress.

## Auth URL hosting recommendation

Host the auth-url endpoint as a small ASP.NET Core API in a cloud service:

- Azure App Service / Azure Container Apps
- AWS ECS/Fargate / Lambda + API Gateway
- Fly.io / Railway / Render

Security rules:

- Keep `Mux:TokenId` and `Mux:TokenSecret` in server-side secret storage only.
- Never expose Mux API credentials in the MAUI app.
- Restrict CORS to your app/web origins where possible.
- Add authentication/rate limiting on `/api/mux/direct-upload-url`.

The sample endpoint implementation is in `samples/Mux.AuthUrl.Api/Program.cs`.

## Publish to GitHub

```bash
git init
git add .
git commit -m "Initial open-source release for Mux MAUI direct upload package and demo"
gh repo create mux-directupload-maui --public --source . --remote origin --push
```

If `gh` is not authenticated, run `gh auth login` first.
