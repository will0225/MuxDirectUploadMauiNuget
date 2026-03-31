# Mux.DirectUpload.Maui

.NET 10 package and demo for uploading videos directly from MAUI apps to Mux using Direct Upload URLs.

## License

This repository is licensed under **Apache-2.0**, matching the Mux iOS and Android Upload SDK license.

## Projects

- `Mux.DirectUpload.Maui` - reusable library/NuGet package.
- `Mux.DirectUpload.Demo` - .NET 10 MAUI demo app (Android + iOS + Windows).
- `samples/Mux.AuthUrl.Api` - sample backend API that creates Direct Upload URLs via Mux API.

## Build

```bash
dotnet build -c Release Mux.DirectUpload.Maui/Mux.DirectUpload.Maui.csproj
dotnet pack -c Release Mux.DirectUpload.Maui/Mux.DirectUpload.Maui.csproj -o artifacts
dotnet build -c Release Mux.DirectUpload.Demo/Mux.DirectUpload.Demo.csproj -f net10.0-windows10.0.19041.0
dotnet build -c Release samples/Mux.AuthUrl.Api/Mux.AuthUrl.Api.csproj
```

## Demo app usage

1. Start your auth-url backend API.
2. In the app, set:
   - Backend base URL, e.g. `https://your-api.example.com`
   - Endpoint path, e.g. `/api/mux/direct-upload-url`
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
