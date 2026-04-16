const {setGlobalOptions} = require("firebase-functions/v2");
const {onRequest} = require("firebase-functions/v2/https");
const {defineSecret} = require("firebase-functions/params");
const logger = require("firebase-functions/logger");

setGlobalOptions({region: "us-central1"});

const muxTokenId = defineSecret("MUX_TOKEN_ID");
const muxTokenSecret = defineSecret("MUX_TOKEN_SECRET");

function readMuxConfig() {
  const tokenId = muxTokenId.value();
  const tokenSecret = muxTokenSecret.value();

  if (!tokenId || !tokenSecret) {
    throw new Error("Missing MUX_TOKEN_ID or MUX_TOKEN_SECRET secrets.");
  }

  return {tokenId, tokenSecret};
}

function getAuth() {
  const admin = require("firebase-admin");
  if (!admin.apps.length) {
    let projectId = process.env.GCLOUD_PROJECT || process.env.GCP_PROJECT;
    if (!projectId && process.env.FIREBASE_CONFIG) {
      try {
        projectId = JSON.parse(process.env.FIREBASE_CONFIG).projectId;
      } catch {
        // ignore
      }
    }
    admin.initializeApp(projectId ? {projectId} : {});
  }
  return admin.auth();
}

async function verifyFirebaseBearerToken(req) {
  const authHeader = req.headers.authorization || "";
  const match = authHeader.match(/^Bearer\s+(.+)$/i);
  if (!match) {
    return {ok: false, status: 401, message: "Missing Bearer token."};
  }

  try {
    const decoded = await getAuth().verifyIdToken(match[1]);
    return {ok: true, uid: decoded.uid};
  } catch (err) {
    logger.warn("Invalid Firebase token", err);
    return {ok: false, status: 401, message: "Invalid Firebase token."};
  }
}

async function createMuxDirectUploadUrl() {
  const {tokenId, tokenSecret} = readMuxConfig();
  const basic = Buffer.from(`${tokenId}:${tokenSecret}`, "utf8")
      .toString("base64");

  const payload = {
    "new_asset_settings": {
      "playback_policy": ["public"],
    },
    "cors_origin": "*",
  };

  const response = await fetch("https://api.mux.com/video/v1/uploads", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "authorization": `Basic ${basic}`,
    },
    body: JSON.stringify(payload),
  });

  const text = await response.text();
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    json = null;
  }

  if (!response.ok) {
    const detail = json?.error?.messages?.join(", ") || text || "Mux API error";
    const err = new Error(detail);
    err.status = 502;
    err.muxStatus = response.status;
    err.muxBody = text?.length > 2000 ? text.slice(0, 2000) + "…" : text;
    throw err;
  }

  const uploadUrl = json?.data?.url;
  if (!uploadUrl) {
    const err = new Error("Mux response missing data.url.");
    err.status = 502;
    err.muxBody = text?.length > 2000 ? text.slice(0, 2000) + "…" : text;
    throw err;
  }

  return uploadUrl;
}

exports.getMuxDirectUploadUrl = onRequest(
    {
      cors: true,
      secrets: [muxTokenId, muxTokenSecret],
    },
    async (req, res) => {
      if (req.method !== "GET") {
        res.status(405).json({error: "Method Not Allowed"});
        return;
      }

      const auth = await verifyFirebaseBearerToken(req);
      if (!auth.ok) {
        res.status(auth.status).json({error: auth.message});
        return;
      }

      try {
        const uploadUrl = await createMuxDirectUploadUrl();
        res.status(200).json({uploadUrl});
      } catch (err) {
        logger.error("Failed to create Mux upload URL", err);
        const body = {
          error: err.message || "Failed to create upload URL.",
        };
        if (err.muxStatus != null) {
          body.muxHttpStatus = err.muxStatus;
        }
        if (err.muxBody != null) {
          body.muxResponseSnippet = err.muxBody;
        }
        res.status(err.status || 500).json(body);
      }
    },
);
