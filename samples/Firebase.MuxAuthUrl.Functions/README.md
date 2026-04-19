# Firebase Mux Auth URL Endpoint (Node.js)

This sample exposes Firebase HTTPS functions for Mux Direct Upload:

- `getMuxDirectUploadUrl` (GET) — returns `{ "uploadUrl": "...", "uploadId": "...", ... }`
- `getMuxUploadStatus` (GET) — Mux upload + asset enrichment (playback ids, meta)
- `muxWebhook` (POST) — [Mux webhooks](https://docs.mux.com/guides/system/listen-for-webhooks): verifies `Mux-Signature`, writes state to Firestore
- `getMuxWebhookStatus` (GET) — reads the last webhook-derived row for an `uploadId` or `assetId` from Firestore

Auth in this repo’s sample may use Firebase ID tokens (see `index.js`); older docs referred to Basic auth for app credentials—align your deployment with how you lock down the GET handlers.

## 1) Prerequisites

- Firebase project with Functions enabled
- Firebase CLI installed and logged in
- Node.js 20+

## 2) Install

```bash
cd samples/Firebase.MuxAuthUrl.Functions
cd functions
npm install
```

## 3) Set secrets

```bash
firebase functions:secrets:set MUX_TOKEN_ID
firebase functions:secrets:set MUX_TOKEN_SECRET
firebase functions:secrets:set MUX_WEBHOOK_SECRET
```

Use the **signing secret** from [Mux → Webhooks](https://dashboard.mux.com/settings/webhooks) for `MUX_WEBHOOK_SECRET` (one secret per webhook URL). Enable **Firestore** (Native mode) in the Firebase project; webhook updates go to collection `muxUploadWebhook`.

After deploy, register the webhook URL in Mux, for example:

`https://<region>-<project>.cloudfunctions.net/muxWebhook`

(or the Cloud Run URL Firebase prints—either works if it targets `muxWebhook`).

`muxWebhook` must accept **unauthenticated** HTTPS calls from Mux’s servers (`invoker: "public"` in code). In Google Cloud Console, confirm the function allows **allUsers** / **Cloud Run Invoker** as needed if deploy does not set it automatically.

## 4) Deploy

Optional: run ESLint from `functions/` before deploy:

```bash
cd functions
npm run lint
```

```bash
cd ..
firebase deploy --only functions
```

Deploy does not run lint automatically (avoids ESLint/parser issues and Windows CLI quirks with predeploy hooks).

After deploy, Firebase prints a URL. It may look like a **Cloud Run** URL (`*.run.app`) or a **cloudfunctions.net** URL; both work if they point at the same function.

### Deployed sample for repo testing

This repository maintains a **public test instance** (GET, returns JSON with `uploadUrl`):

`https://getmuxdirectuploadurl-fy5arhfiaq-uc.a.run.app/`

Use that value as the HTTP **base URL** in clients. For `HttpMuxAuthUrlProvider`, set the relative **path** to `/` (the handler is mounted at the service root). The test instance may be rate-limited or rotated; deploy your own function for production.

## 5) Call from your MAUI app

Use your deployed function URL as the `HttpClient.BaseAddress` in your MAUI app. If the URL ends at the function root (as with the shared test URL above), set:

- `endpointPath` to `/`

If your URL includes a path segment such as `/getMuxDirectUploadUrl`, use that as `endpointPath` instead.

Your MAUI request may use Basic auth (if you implement it on the function):

`Authorization: Basic <base64(username:password)>`

With this repo's package, use `BasicAuthMuxAuthUrlProvider`.

To consume **webhook-backed** status, call `getMuxWebhookStatus?uploadId=<id>` on the same deployed base (or add a dedicated client) after you upload.

## Deploy: "Timeout after 10000" / "Cannot determine backend specification"

This happens while the Firebase CLI analyzes your function code. Try in order:

1. **Upgrade the Firebase CLI** (older CLIs choke on Functions v2 + params):  
   `npm install -g firebase-tools@latest`  
   Then run `firebase --version` (use a recent 13.x+).

2. **Reinstall dependencies** in `functions/`: delete `node_modules` and `package-lock.json`, then `npm install`.

3. **Deploy with debug logs** and share the last lines if it still fails:  
   `firebase deploy --only functions --debug`

4. **On Windows**, if deploy keeps timing out, run the same command from **WSL2** or **Git Bash**, or another machine; some environments block or slow the Node subprocess the CLI uses for code discovery.

5. Confirm required secrets exist in Secret Manager: `MUX_TOKEN_ID`, `MUX_TOKEN_SECRET`, and `MUX_WEBHOOK_SECRET` if you deploy `muxWebhook`.

## Notes

- This sample keeps `playback_policy` as `public` to match demo behavior.
- Webhooks are **server-side only**: the app never sees the webhook signing secret. After upload, the client can poll `getMuxWebhookStatus?uploadId=...` (or your own API) instead of hammering `getMuxUploadStatus`.
- For production, consider:
  - private/signed playback policy
  - stricter CORS (and auth on `getMuxWebhookStatus` so upload IDs are not enumerable)
  - rate limiting and abuse controls
