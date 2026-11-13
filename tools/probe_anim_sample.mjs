// tools/probe_anim_sample.mjs
import fs from "node:fs";
import path from "node:path";
import http from "node:http";
import https from "node:https";

const localAppData = process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE, "AppData", "Local");
const catalogPath = path.join(localAppData, "LexBrowser", "tv_catalog_cache.json");

const catalogRaw = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
const channels = catalogRaw.channels || catalogRaw.Channels || [];

const animChannels = channels.filter(c => {
  const g = (c.group || c.Group || "").toLowerCase();
  return g === "animation" || g === "animación" || g.includes("animat") || g.includes("anime");
});

console.log(`Encontrados ${animChannels.length} canales de animación. Tomando muestra de hasta 100...`);
const sample = animChannels.slice(0, 100);

function probeUrl(url, referrer, userAgent) {
  return new Promise((resolve) => {
    let t0 = Date.now();
    let client;
    try {
      const u = new URL(url);
      client = u.protocol === "https:" ? https : http;
    } catch (e) {
      return resolve({
        status: 400,
        kind: "unknown",
        engine: "none",
        tFirstChunkMs: 0,
        errorClass: "invalid_url",
        errorMsg: e.message
      });
    }

    const req = client.request(url, {
      method: "GET",
      headers: {
        "User-Agent": userAgent || "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LexBrowser/2.1",
        "Referer": referrer || "",
        "Range": "bytes=0-4095"
      },
      timeout: 4000
    }, (res) => {
      let firstByteTime = Date.now() - t0;
      let contentType = res.headers["content-type"] || "";
      let chunks = [];
      let totalBytes = 0;

      // Seguir redirección 301/302 una vez si procede
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        req.destroy();
        return resolve(probeUrl(res.headers.location, referrer, userAgent));
      }

      res.on("data", (chunk) => {
        if (totalBytes === 0) firstByteTime = Date.now() - t0;
        chunks.push(chunk);
        totalBytes += chunk.length;
        if (totalBytes >= 4096) {
          req.destroy();
        }
      });

      res.on("end", () => finish());
      res.on("close", () => finish());

      function finish() {
        const buf = Buffer.concat(chunks);
        const ct = contentType.toLowerCase();
        let kind = "unknown";
        let engine = "hls.js";

        const textPreview = buf.slice(0, 300).toString("utf8");

        if (textPreview.includes("#EXTM3U") || ct.includes("mpegurl") || ct.includes("application/x-mpegurl") || url.includes(".m3u8")) {
          kind = "HLS";
          engine = "Hls.js";
        } else if (buf.length > 0 && buf[0] === 0x47) {
          kind = "MpegTS";
          engine = "mpegts.js";
        } else if (buf.length >= 8 && buf.slice(4, 8).toString("latin1") === "ftyp") {
          kind = "MP4";
          engine = "video nativo";
        } else if (textPreview.toLowerCase().includes("<html")) {
          kind = "HTML (Geo/Landing)";
          engine = "none";
        }

        let errorClass = "none";
        if (res.statusCode >= 400) {
          errorClass = `http_${res.statusCode}`;
        } else if (kind === "HTML (Geo/Landing)") {
          errorClass = "landing_or_geoblock";
        }

        resolve({
          status: res.statusCode,
          contentType,
          kind,
          engine,
          tFirstChunkMs: firstByteTime,
          errorClass,
          bytesReceived: totalBytes
        });
      }
    });

    req.on("timeout", () => {
      req.destroy();
      resolve({
        status: 504,
        kind: "unknown",
        engine: "none",
        tFirstChunkMs: Date.now() - t0,
        errorClass: "timeout",
        errorMsg: "Timeout > 4s"
      });
    });

    req.on("error", (err) => {
      resolve({
        status: 502,
        kind: "unknown",
        engine: "none",
        tFirstChunkMs: Date.now() - t0,
        errorClass: err.code || "upstream_err",
        errorMsg: err.message
      });
    });

    req.end();
  });
}

async function probeSample() {
  const results = [];
  const CONCURRENCY = 6;
  let idx = 0;

  async function worker() {
    while (idx < sample.length) {
      const currentIdx = idx++;
      const ch = sample[currentIdx];
      const url = ch.url || ch.Url;
      const ref = ch.referrer || ch.Referrer;
      const ua = ch.userAgent || ch.UserAgent;

      process.stdout.write(`[${currentIdx + 1}/${sample.length}] Probing: ${ch.name || ch.Name} ... `);
      const probe = await probeUrl(url, ref, ua);
      console.log(`${probe.status} (${probe.kind}, ${probe.tFirstChunkMs}ms, error: ${probe.errorClass})`);

      results.push({
        name: ch.name || ch.Name,
        country: ch.country || ch.Country || "??",
        language: ch.language || ch.Language || "??",
        url: url,
        status: probe.status,
        kind: probe.kind,
        engine: probe.engine,
        tFirstChunkMs: probe.tFirstChunkMs,
        errorClass: probe.errorClass
      });
    }
  }

  const workers = Array.from({ length: CONCURRENCY }, () => worker());
  await Promise.all(workers);

  console.log("\n=== RESUMEN DEL DIAGNÓSTICO DE ANIMACIÓN (100 CANALES) ===");
  const okCount = results.filter(r => r.status >= 200 && r.status < 300 && r.errorClass === "none").length;
  const timeoutCount = results.filter(r => r.errorClass === "timeout").length;
  const http403Count = results.filter(r => r.status === 403 || r.errorClass === "http_403").length;
  const http404Count = results.filter(r => r.status === 404 || r.errorClass === "http_404").length;
  const dnsTlsCount = results.filter(r => r.errorClass.includes("ENOTFOUND") || r.errorClass.includes("CERT") || r.errorClass.includes("ECONN")).length;
  const geoLandingCount = results.filter(r => r.errorClass === "landing_or_geoblock").length;
  const otherFail = results.length - okCount - timeoutCount - http403Count - http404Count - dnsTlsCount - geoLandingCount;

  console.log(`Total probados: ${results.length}`);
  console.log(`Activos / Reproducibles (2xx HLS/TS): ${okCount} (${((okCount / results.length) * 100).toFixed(1)}%)`);
  console.log(`Timeout (>4s): ${timeoutCount}`);
  console.log(`HTTP 403 (Token/Geo/Hotlink): ${http403Count}`);
  console.log(`HTTP 404 (Caído / Stream no existe): ${http404Count}`);
  console.log(`DNS / Conexión / TLS fallido: ${dnsTlsCount}`);
  console.log(`HTML Landing / Captcha / Geoblock: ${geoLandingCount}`);
  console.log(`Otros fallos: ${otherFail}`);

  fs.writeFileSync("docs/fase1_probe_anim_sample.json", JSON.stringify(results, null, 2), "utf8");
}

probeSample();

