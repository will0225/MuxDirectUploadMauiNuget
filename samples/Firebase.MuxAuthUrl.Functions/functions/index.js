const crypto = require("crypto");
const {setGlobalOptions} = require("firebase-functions/v2");
const {onRequest} = require("firebase-functions/v2/https");
const {defineSecret} = require("firebase-functions/params");
const logger = require("firebase-functions/logger");

setGlobalOptions({region: "us-central1"});

const muxTokenId = defineSecret("MUX_TOKEN_ID");
const muxTokenSecret = defineSecret("MUX_TOKEN_SECRET");
const muxWebhookSecret = defineSecret("MUX_WEBHOOK_SECRET");

function readMuxConfig() {
  const tokenId = muxTokenId.value();
  const tokenSecret = muxTokenSecret.value();

  if (!tokenId || !tokenSecret) {
    throw new Error("Missing MUX_TOKEN_ID or MUX_TOKEN_SECRET secrets.");
  }

  return {tokenId, tokenSecret};
}

function ensureAdminApp() {
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
  return admin;
}

function getAuth() {
  return ensureAdminApp().auth();
}

function getFirestore() {
  return ensureAdminApp().firestore();
}

/** @see https://docs.mux.com/docs/core/verify-webhook-signatures */
function verifyMuxWebhookSignature(rawBody, signatureHeader, secret) {
  if (!rawBody || !secret) {
    return {ok: false, reason: "missing_body_or_secret"};
  }
  const sig = signatureHeader ? String(signatureHeader) : "";
  let t;
  let v1;
  for (const part of sig.split(",")) {
    const eq = part.indexOf("=");
    if (eq === -1) continue;
    const key = part.slice(0, eq).trim();
    const val = part.slice(eq + 1).trim();
    if (key === "t") t = val;
    if (key === "v1") v1 = val;
  }
  if (!t || !v1) {
    return {ok: false, reason: "bad_signature_format"};
  }
  const ts = parseInt(t, 10);
  if (Number.isNaN(ts) || Math.abs(Math.floor(Date.now() / 1000) - ts) > 300) {
    return {ok: false, reason: "timestamp_skew"};
  }
  const bodyStr = Buffer.isBuffer(rawBody) ? rawBody.toString("utf8") : String(rawBody);
  const signedPayload = `${t}.${bodyStr}`;
  const expectedHex = crypto
      .createHmac("sha256", secret)
      .update(signedPayload, "utf8")
      .digest("hex");
  try {
    const a = Buffer.from(expectedHex, "hex");
    const b = Buffer.from(v1, "hex");
    if (a.length !== b.length) {
      return {ok: false, reason: "signature_length"};
    }
    if (!crypto.timingSafeEqual(a, b)) {
      return {ok: false, reason: "signature_mismatch"};
    }
  } catch {
    return {ok: false, reason: "signature_compare"};
  }
  return {ok: true};
}

function extractMuxWebhookFields(payload) {
  const type = payload?.type || "";
  const data = payload?.data && typeof payload.data === "object" ? payload.data : {};
  let uploadId = data.upload_id != null ? String(data.upload_id) : null;
  let assetId = data.asset_id != null ? String(data.asset_id) : null;

  if (type.startsWith("video.upload.")) {
    if (!uploadId && data.id != null) uploadId = String(data.id);
  }
  if (type.startsWith("video.asset.")) {
    if (data.id != null) assetId = String(data.id);
    if (data.upload_id != null) uploadId = String(data.upload_id);
  }

  const playbackIds = Array.isArray(data.playback_ids) ? data.playback_ids : null;
  const playbackId =
    playbackIds && playbackIds.length > 0 && playbackIds[0]?.id ?
      String(playbackIds[0].id) :
      null;
  const assetStatus = data.status != null ? String(data.status) : null;
  const passthrough = data.passthrough != null ? String(data.passthrough) : null;

  return {
    type,
    uploadId,
    assetId,
    playbackId,
    playbackIds,
    assetStatus,
    passthrough,
  };
}

async function persistMuxWebhookDoc(fields) {
  const admin = ensureAdminApp();
  const {type, uploadId, assetId, playbackId, playbackIds, assetStatus, passthrough} = fields;
  let docId = uploadId && String(uploadId).trim();
  if (!docId && assetId) {
    docId = `asset_${String(assetId).trim()}`;
  }
  if (!docId) {
    docId = `evt_${Date.now()}_${Math.random().toString(36).slice(2, 10)}`;
  }

  const ref = getFirestore().collection("muxUploadWebhook").doc(docId);
  const playbackIdsPlain = playbackIds ?
    playbackIds.map((p) => (p && typeof p === "object" ? {...p} : p)) :
    null;

  await ref.set(
      {
        updatedAt: admin.firestore.FieldValue.serverTimestamp(),
        lastEventType: type,
        uploadId: uploadId || null,
        assetId: assetId || null,
        playbackId: playbackId || null,
        playbackIds: playbackIdsPlain,
        assetStatus: assetStatus || null,
        passthrough: passthrough || null,
      },
      {merge: true},
  );
}

async function verifyFirebaseIdToken(req) {
  let idToken = null;
  const authHeader = req.headers.authorization || "";
  const bearerMatch = authHeader.match(/^Bearer\s+(.+)$/i);
  if (bearerMatch) {
    idToken = bearerMatch[1];
  } else {
    const q = req.query || {};
    idToken = q.token || q.id_token || null;
    if (idToken != null) {
      idToken = String(idToken);
    }
  }

  if (!idToken) {
    return {
      ok: false,
      status: 401,
      message: "Missing Firebase ID token (Authorization: Bearer or ?token= / ?id_token=).",
    };
  }

  try {
    const decoded = await getAuth().verifyIdToken(idToken);
    return {ok: true, uid: decoded.uid};
  } catch (err) {
    logger.warn("Invalid Firebase token", err);
    return {ok: false, status: 401, message: "Invalid Firebase token."};
  }
}

function decodeBase64JsonObject(raw, labelForLog) {
  if (!raw) return null;
  try {
    const json = Buffer.from(String(raw), "base64").toString("utf8");
    const obj = JSON.parse(json);
    if (obj && typeof obj === "object" && !Array.isArray(obj)) {
      return obj;
    }
  } catch (e) {
    logger.warn("Invalid base64 JSON (" + labelForLog + ")", e);
  }
  return null;
}

function decodeUploadQualitySettings(raw) {
  return decodeBase64JsonObject(raw, "upload quality");
}

function decodeAssetMetadata(raw) {
  return decodeBase64JsonObject(raw, "asset metadata");
}

function readAuthContext(req) {
  const h = req.headers || {};
  const creatorId =
    h["mux-auth-creator-id"] ||
    req.query.creatorId ||
    "";
  const externalId =
    h["mux-auth-external-id"] ||
    req.query.externalId ||
    "";

  const qualityRaw =
    h["mux-auth-upload-quality-settings"] ||
    req.query.uploadQualitySettings ||
    "";

  const metadataRaw =
    h["mux-auth-asset-metadata"] ||
    req.query.assetMetadata ||
    "";

  return {
    creatorId: creatorId ? String(creatorId) : "",
    externalId: externalId ? String(externalId) : "",
    uploadQualitySettings: decodeUploadQualitySettings(qualityRaw),
    assetMetadata: decodeAssetMetadata(metadataRaw),
  };
}

function buildNewAssetSettings({creatorId, externalId, uploadQualitySettings, assetMetadata, authUid}) {
  const newAssetSettings = {
    playback_policy: ["public"],
  };

  if (uploadQualitySettings && typeof uploadQualitySettings === "object") {
    Object.assign(newAssetSettings, uploadQualitySettings);
  }

  const fromClient = assetMetadata && typeof assetMetadata === "object" && !Array.isArray(assetMetadata) ?
    {...assetMetadata} :
    {};

  const passthroughFromMeta = fromClient.passthrough;
  delete fromClient.passthrough;

  const meta = {
    ...(newAssetSettings.meta && typeof newAssetSettings.meta === "object" && !Array.isArray(newAssetSettings.meta) ?
      newAssetSettings.meta :
      {}),
    ...fromClient,
  };

  if (creatorId) meta.creator_id = String(creatorId);
  if (externalId) meta.external_id = String(externalId);

  const metaOut = {};
  for (const [k, v] of Object.entries(meta)) {
    if (v !== undefined && v !== null) {
      metaOut[k] = typeof v === "string" ? v : String(v);
    }
  }

  if (Object.keys(metaOut).length) {
    newAssetSettings.meta = metaOut;
  } else {
    delete newAssetSettings.meta;
  }

  if (passthroughFromMeta !== undefined && passthroughFromMeta !== null && String(passthroughFromMeta).length > 0) {
    newAssetSettings.passthrough = String(passthroughFromMeta).slice(0, 255);
  } else if (authUid) {
    newAssetSettings.passthrough = String(authUid).slice(0, 255);
  }

  return newAssetSettings;
}

async function fetchFirstPlaybackId(assetId, basic) {
  if (!assetId) return null;
  try {
    const r = await fetch(
        `https://api.mux.com/video/v1/assets/${encodeURIComponent(assetId)}`,
        {headers: {authorization: `Basic ${basic}`}},
    );
    if (!r.ok) return null;
    const body = await r.json();
    const list = body?.data?.playback_ids;
    if (!Array.isArray(list) || list.length === 0) return null;
    const first = list[0];
    return first?.id ? String(first.id) : null;
  } catch (e) {
    logger.warn("Could not fetch asset for playback id", e);
    return null;
  }
}

async function createMuxDirectUploadUrl({creatorId, externalId, uploadQualitySettings, assetMetadata, authUid} = {}) {
  const {tokenId, tokenSecret} = readMuxConfig();
  const basic = Buffer.from(`${tokenId}:${tokenSecret}`, "utf8")
      .toString("base64");

  const newAssetSettings = buildNewAssetSettings({
    creatorId,
    externalId,
    uploadQualitySettings,
    assetMetadata,
    authUid,
  });

  const payload = {
    new_asset_settings: newAssetSettings,
    cors_origin: "*",
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
    err.muxBody = text?.length > 2000 ? text.slice(0, 2000) + "..." : text;
    throw err;
  }

  const d = json?.data;
  const uploadUrl = d?.url;
  if (!uploadUrl) {
    const err = new Error("Mux response missing data.url.");
    err.status = 502;
    err.muxBody = text?.length > 2000 ? text.slice(0, 2000) + "..." : text;
    throw err;
  }

  const uploadId = d?.id != null ? String(d.id) : null;
  const assetId = d?.asset_id != null ? String(d.asset_id) : null;

  let playbackId = null;
  if (assetId) {
    playbackId = await fetchFirstPlaybackId(assetId, basic);
  }

  return {
    uploadUrl,
    uploadId,
    assetId,
    playbackId,
  };
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

      // const auth = await verifyFirebaseIdToken(req);
      // if (!auth.ok) {
      //   res.status(auth.status).json({error: auth.message});
      //   return;
      // }

      try {
        const ctx = readAuthContext(req);

        const body = await createMuxDirectUploadUrl({
          creatorId: ctx.creatorId,
          externalId: ctx.externalId,
          uploadQualitySettings: ctx.uploadQualitySettings,
          assetMetadata: ctx.assetMetadata,
          // authUid: auth.uid,
          authUid: "panda0225"
        });
        res.status(200).json(body);
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

async function muxGetUpload(uploadId, basic) {
  const r = await fetch(
      `https://api.mux.com/video/v1/uploads/${encodeURIComponent(uploadId)}`,
      {
        headers: {
          "content-type": "application/json",
          "authorization": `Basic ${basic}`,
        },
      },
  );
  const text = await r.text();
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    json = null;
  }
  if (!r.ok) {
    const detail = json?.error?.messages?.join(", ") || text || "Mux API error";
    const err = new Error(detail);
    err.status = r.status === 404 ? 404 : 502;
    err.muxStatus = r.status;
    err.muxBody = text?.length > 2000 ? text.slice(0, 2000) + "..." : text;
    throw err;
  }
  return json?.data ?? json;
}

/** GET /video/v1/assets/{id} — playback_ids live here, not on the upload object. */
async function muxGetAsset(assetId, basic) {
  const r = await fetch(
      `https://api.mux.com/video/v1/assets/${encodeURIComponent(assetId)}`,
      {
        headers: {
          "content-type": "application/json",
          "authorization": `Basic ${basic}`,
        },
      },
  );
  const text = await r.text();
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    json = null;
  }
  if (!r.ok) {
    const detail = json?.error?.messages?.join(", ") || text || "Mux API error";
    const err = new Error(detail);
    err.status = r.status === 404 ? 404 : 502;
    err.muxStatus = r.status;
    err.muxBody = text?.length > 2000 ? text.slice(0, 2000) + "..." : text;
    throw err;
  }
  return json?.data ?? json;
}

/**
 * Upload JSON includes new_asset_settings (meta, passthrough). Playback ids are only on the Asset.
 */
async function enrichUploadWithAsset(uploadData, basic) {
  if (!uploadData || typeof uploadData !== "object") {
    return uploadData;
  }
  const out = {...uploadData};
  const aid = uploadData.asset_id;
  if (!aid) {
    out.playbackId = null;
    out.playback_ids = null;
    out.asset_status = null;
    out.asset_meta = null;
    return out;
  }

  try {
    const asset = await muxGetAsset(String(aid), basic);
    out.playback_ids = asset?.playback_ids ?? null;
    const list = out.playback_ids;
    out.playbackId = Array.isArray(list) && list.length > 0 && list[0]?.id ?
      String(list[0].id) :
      null;
    out.asset_status = asset?.status != null ? String(asset.status) : null;
    out.asset_meta = asset?.meta && typeof asset.meta === "object" ?
      Object.fromEntries(
          Object.entries(asset.meta).map(([k, v]) => [k, v == null ? "" : String(v)]),
      ) :
      null;
  } catch (e) {
    logger.warn("enrichUploadWithAsset: could not load asset", e);
    out.playbackId = null;
    out.playback_ids = null;
    out.asset_status = null;
    out.asset_meta = null;
  }
  return out;
}

exports.getMuxUploadStatus = onRequest(
    {
      cors: true,
      secrets: [muxTokenId, muxTokenSecret],
    },
    async (req, res) => {
      if (req.method !== "GET") {
        res.status(405).json({error: "Method Not Allowed"});
        return;
      }

      // const auth = await verifyFirebaseIdToken(req);
      // if (!auth.ok) {
      //   res.status(auth.status).json({error: auth.message});
      //   return;
      // }

      const uploadId = req.query.uploadId || req.query.upload_id;
      if (!uploadId || !String(uploadId).trim()) {
        res.status(400).json({error: "Missing uploadId query parameter."});
        return;
      }

      try {
        const {tokenId, tokenSecret} = readMuxConfig();
        const basic = Buffer.from(`${tokenId}:${tokenSecret}`, "utf8")
            .toString("base64");
        const data = await muxGetUpload(String(uploadId).trim(), basic);
        const enriched = await enrichUploadWithAsset(data, basic);
        res.status(200).json(enriched);
      } catch (err) {
        logger.error("Failed to get Mux upload", err);
        const body = {
          error: err.message || "Failed to get upload.",
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

function serializeMuxWebhookDoc(data) {
  if (!data || typeof data !== "object") {
    return data;
  }
  const out = {...data};
  if (out.updatedAt && typeof out.updatedAt.toDate === "function") {
    out.updatedAt = out.updatedAt.toDate().toISOString();
  }
  return out;
}

/**
 * POST from Mux: verifies MUX_WEBHOOK_SECRET (HMAC) and merges into Firestore `muxUploadWebhook`.
 * Configure this URL in the Mux dashboard; allow unauthenticated invocation on this function.
 */
exports.muxWebhook = onRequest(
    {
      secrets: [muxWebhookSecret],
      invoker: "public",
    },
    async (req, res) => {
      if (req.method !== "POST") {
        res.status(405).send("Method Not Allowed");
        return;
      }
      const secret = muxWebhookSecret.value();
      if (!secret) {
        logger.error("muxWebhook: MUX_WEBHOOK_SECRET not configured");
        res.status(500).json({error: "Server misconfigured."});
        return;
      }
      const rawBody = req.rawBody;
      if (!rawBody) {
        logger.error("muxWebhook: req.rawBody missing (signature verification requires raw body)");
        res.status(400).json({error: "Missing raw body."});
        return;
      }
      const sig =
        req.headers["mux-signature"] ||
        req.headers["Mux-Signature"];
      const verified = verifyMuxWebhookSignature(rawBody, sig, secret);
      if (!verified.ok) {
        logger.warn("muxWebhook: invalid signature", verified.reason);
        res.status(403).json({error: "Invalid signature."});
        return;
      }
      let payload;
      try {
        payload = JSON.parse(Buffer.isBuffer(rawBody) ? rawBody.toString("utf8") : String(rawBody));
      } catch (e) {
        res.status(400).json({error: "Invalid JSON."});
        return;
      }
      try {
        const fields = extractMuxWebhookFields(payload);
        await persistMuxWebhookDoc(fields);
        res.status(204).send();
      } catch (err) {
        logger.error("muxWebhook: persist failed", err);
        res.status(500).json({error: err.message || "Persist failed."});
      }
    },
);

/** GET last webhook-derived fields for an upload (or asset) from Firestore. */
exports.getMuxWebhookStatus = onRequest(
    {
      cors: true,
    },
    async (req, res) => {
      if (req.method !== "GET") {
        res.status(405).json({error: "Method Not Allowed"});
        return;
      }
      const uploadId = req.query.uploadId || req.query.upload_id;
      const assetId = req.query.assetId || req.query.asset_id;
      const uid = uploadId && String(uploadId).trim();
      const aid = assetId && String(assetId).trim();
      if (!uid && !aid) {
        res.status(400).json({error: "Missing uploadId or assetId query parameter."});
        return;
      }
      try {
        const db = getFirestore();
        let snap = null;
        if (uid) {
          snap = await db.collection("muxUploadWebhook").doc(uid).get();
        }
        if ((!snap || !snap.exists) && aid) {
          snap = await db.collection("muxUploadWebhook").doc(`asset_${aid}`).get();
        }
        if (!snap || !snap.exists) {
          res.status(404).json({error: "No webhook data yet."});
          return;
        }
        res.status(200).json(serializeMuxWebhookDoc(snap.data()));
      } catch (err) {
        logger.error("getMuxWebhookStatus", err);
        res.status(500).json({error: err.message || "Failed to read webhook status."});
      }
    },
);
