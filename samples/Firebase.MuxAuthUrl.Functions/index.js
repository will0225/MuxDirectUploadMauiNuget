const { onRequest } = require("firebase-functions/v2/https");
const logger = require("firebase-functions/logger");
const admin = require("firebase-admin");

admin.initializeApp();

function readMuxConfig() {
  const tokenId = process.env.MUX_TOKEN_ID;
  const tokenSecret = process.env.MUX_TOKEN_SECRET;

  if (!tokenId || !tokenSecret) {
    throw new Error("Missing MUX_TOKEN_ID or MUX_TOKEN_SECRET secrets.");
  }

  return { tokenId, tokenSecret };
}

async function verifyFirebaseBearerToken(req) {
  const authHeader = req.headers.authorization || "";
  const match = authHeader.match(/^Bearer\s+(.+)$/i);
  if (!match) {
    return { ok: false, status: 401, message: "Missing Bearer token." };
  }

  try {
    const decoded = await admin.auth().verifyIdToken(match[1]);
    return { ok: true, uid: decoded.uid };
  } catch (err) {
    logger.warn("Invalid Firebase token", err);
    return { ok: false, status: 401, message: "Invalid Firebase token." };
  }
}

async function createMuxDirectUploadUrl() {
  const { tokenId, tokenSecret } = readMuxConfig();
  const basic = Buffer.from(`${tokenId}:${tokenSecret}`, "utf8").toString("base64");

  const payload = {
    cors_origin: "*",
    timeout: "3600s",
    new_asset_settings: {
      playback_policy: ["public"]
    }
  };

  const response = await fetch("https://api.mux.com/video/v1/uploads", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      authorization: `Basic ${basic}`
    },
    body: JSON.stringify(payload)
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
    throw err;
  }

  const uploadUrl = json?.data?.url;
  if (!uploadUrl) {
    const err = new Error("Mux response missing data.url.");
    err.status = 502;
    throw err;
  }

  return uploadUrl;
}

exports.getMuxDirectUploadUrl = onRequest(
  {
    region: "us-central1",
    cors: true,
    secrets: ["MUX_TOKEN_ID", "MUX_TOKEN_SECRET"]
  },
  async (req, res) => {
    if (req.method !== "GET") {
      res.status(405).json({ error: "Method Not Allowed" });
      return;
    }

    const auth = await verifyFirebaseBearerToken(req);
    if (!auth.ok) {
      res.status(auth.status).json({ error: auth.message });
      return;
    }

    try {
      const uploadUrl = await createMuxDirectUploadUrl();
      res.status(200).json({ uploadUrl });
    } catch (err) {
      logger.error("Failed to create Mux upload URL", err);
      res.status(err.status || 500).json({
        error: err.message || "Failed to create upload URL."
      });
    }
  }
);
