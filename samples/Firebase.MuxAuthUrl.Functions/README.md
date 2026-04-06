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

```bash
cd ..
firebase deploy --only functions:getMuxDirectUploadUrl
```

After deploy, Firebase prints a URL like:

`https://us-central1-<project-id>.cloudfunctions.net/getMuxDirectUploadUrl`

## 5) Call from your MAUI app

Use this Firebase function URL as the `HttpClient.BaseAddress` in your MAUI app, and set:

- `endpointPath` to `/getMuxDirectUploadUrl`

Your MAUI request must include the Firebase ID token:

`Authorization: Bearer <firebase-id-token>`

## Notes

- This sample keeps `playback_policy` as `public` to match demo behavior.
- For production, consider:
  - private/signed playback policy
  - stricter CORS
  - rate limiting and abuse controls
