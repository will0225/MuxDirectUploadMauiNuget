# Firebase Mux Auth URL Endpoint (Node.js)

This sample exposes a Firebase HTTPS function that returns a Mux Direct Upload URL:

- Endpoint: `getMuxDirectUploadUrl` (GET)
- Response: `{ "uploadUrl": "https://storage.googleapis.com/..." }`
- Auth: requires a valid Firebase ID token in `Authorization: Bearer <token>`

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

## 3) Set Mux secrets

```bash
firebase functions:secrets:set MUX_TOKEN_ID
firebase functions:secrets:set MUX_TOKEN_SECRET
```

## 4) Deploy

Optional: run ESLint from `functions/` before deploy:

```bash
cd functions
npm run lint
```

```bash
cd ..
firebase deploy --only functions:getMuxDirectUploadUrl
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

Your MAUI request must include the Firebase ID token:

`Authorization: Bearer <firebase-id-token>`

## Deploy: "Timeout after 10000" / "Cannot determine backend specification"

This happens while the Firebase CLI analyzes your function code. Try in order:

1. **Upgrade the Firebase CLI** (older CLIs choke on Functions v2 + params):  
   `npm install -g firebase-tools@latest`  
   Then run `firebase --version` (use a recent 13.x+).

2. **Reinstall dependencies** in `functions/`: delete `node_modules` and `package-lock.json`, then `npm install`.

3. **Deploy with debug logs** and share the last lines if it still fails:  
   `firebase deploy --only functions:getMuxDirectUploadUrl --debug`

4. **On Windows**, if deploy keeps timing out, run the same command from **WSL2** or **Git Bash**, or another machine; some environments block or slow the Node subprocess the CLI uses for code discovery.

5. Confirm **Mux secrets exist** in Secret Manager (`MUX_TOKEN_ID`, `MUX_TOKEN_SECRET`) and the project is on the **Blaze** plan.

## Notes

- This sample keeps `playback_policy` as `public` to match demo behavior.
- For production, consider:
  - private/signed playback policy
  - stricter CORS
  - rate limiting and abuse controls
