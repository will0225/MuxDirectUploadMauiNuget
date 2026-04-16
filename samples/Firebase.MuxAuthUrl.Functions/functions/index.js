const {setGlobalOptions} = require("firebase-functions/v2");
const {onRequest} = require("firebase-functions/v2/https");
const {defineSecret} = require("firebase-functions/params");
const logger = require("firebase-functions/logger");

setGlobalOptions({region: "us-central1"});

const muxTokenId = defineSecret("MUX_TOKEN_ID");
const muxTokenSecret = defineSecret("MUX_TOKEN_SECRET");
const appUsername = defineSecret("APP_USERNAME");
const appPassword = defineSecret("APP_PASSWORD");

function readMuxConfig() {
  const tokenId = muxTokenId.value();
  const tokenSecret = muxTokenSecret.value();

  if (!tokenId || !tokenSecret) {
    throw new Error("Missing MUX_TOKEN_ID or MUX_TOKEN_SECRET secrets.");
  }

  return {tokenId, tokenSecret};
}

function readAppCredentials() {
  const username = appUsername.value();
  const password = appPassword.value();

  if (!username || !password) {
    throw new Error("Missing APP_USERNAME or APP_PASSWORD secrets.");
  }

  return {username, password};
}

function verifyBasicCredentials(req) {
  const authHeader = req.headers.authorization || "";
  const match = authHeader.match(/^Basic\s+(.+)$/i);
  if (!match) {
    return {ok: false, status: 401, message: "Missing Basic authorization header."};
  }

  try {
    const decoded = Buffer.from(match[1], "base64").toString("utf8");
    const separatorIndex = decoded.indexOf(":");
    if (separatorIndex < 0) {
      return {ok: false, status: 401, message: "Invalid Basic authorization header."};
    }

    const providedUsername = decoded.slice(0, separatorIndex);
    const providedPassword = decoded.slice(separatorIndex + 1);
    const expected = readAppCredentials();

    if (providedUsername !== expected.username || providedPassword !== expected.password) {
      return {ok: false, status: 401, message: "Invalid username or password."};
    }

    return {ok: true};
  } catch (err) {
    logger.warn("Invalid basic credentials", err);
    return {ok: false, status: 401, message: "Invalid Basic authorization header."};
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
      secrets: [muxTokenId, muxTokenSecret, appUsername, appPassword],
    },
    async (req, res) => {
      if (req.method !== "GET") {
        res.status(405).json({error: "Method Not Allowed"});
        return;
      }

      const auth = verifyBasicCredentials(req);
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
