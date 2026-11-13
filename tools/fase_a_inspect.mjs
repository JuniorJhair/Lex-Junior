// tools/fase_a_inspect.mjs
import fs from "node:fs";
import path from "node:path";

const localAppData = process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE, "AppData", "Local");
const catalogPath = path.join(localAppData, "LexBrowser", "tv_catalog_cache.json");

const catalogRaw = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
const localChannels = catalogRaw.channels || catalogRaw.Channels || [];

const iptvIdSet = new Set();
const iptvUrlSet = new Set();

function normalizeStreamUrl(u) {
  if (!u) return "";
  try {
    const parsed = new URL(u.trim());
    return `${parsed.protocol}//${parsed.host}${parsed.pathname}`.toLowerCase();
  } catch {
    return u.trim().toLowerCase();
  }
}

for (const c of localChannels) {
  const id = (c.id || c.Id || "").trim();
  if (id) iptvIdSet.add(id.toLowerCase());
  const u = normalizeStreamUrl(c.url || c.Url || "");
  if (u) iptvUrlSet.add(u);
  if (c.Alternates || c.alternates) {
    for (const alt of (c.Alternates || c.alternates)) {
      const au = normalizeStreamUrl(alt.Url || alt.url);
      if (au) iptvUrlSet.add(au);
    }
  }
}

console.log(`IPTV-org local: ${iptvIdSet.size} tvg-ids únicos, ${iptvUrlSet.size} URLs normalizadas únicas.`);

const SOURCES = [
  { name: "legalFree live.m3u", url: "https://biskyt.github.io/legalFreeIPTVplaylist/live.m3u" },
  { name: "Free-TV playlist.m3u8", url: "https://raw.githubusercontent.com/Free-TV/IPTV/master/playlist.m3u8" },
  { name: "Archive Watch VOD", url: "https://archivewatch.org/feeds/archivewatch.m3u" }
];

async function fetchSource(src) {
  console.log(`\nDescargando ${src.name} desde ${src.url} ...`);
  try {
    const res = await fetch(src.url, { headers: { "User-Agent": "LexBrowser/2.1 (Inspection)" }, signal: AbortSignal.timeout(20000) });
    console.log(`  Status: ${res.status} ${res.statusText}`);
    if (!res.ok) {
      return { src, error: `HTTP ${res.status}`, status: res.status };
    }
    const text = await res.text();
    console.log(`  Tamaño: ${(text.length / 1024).toFixed(1)} KB (${text.split('\n').length} líneas)`);
    return { src, text, status: res.status };
  } catch (err) {
    console.error(`  Error al descargar: ${err.message}`);
    return { src, error: err.message };
  }
}

function parseM3uSimple(content) {
  const lines = content.split(/\r?\n/);
  const items = [];
  let currentInfo = null;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].trim();
    if (!line) continue;

    if (line.startsWith("#EXTINF:")) {
      currentInfo = { raw: line, attrs: {} };
      // Extraer nombre (después de la última coma)
      const lastComma = line.lastIndexOf(",");
      if (lastComma >= 0) {
        currentInfo.name = line.substring(lastComma + 1).trim();
      }

      // Extraer atributos clave="valor"
      const attrRegex = /([a-zA-Z0-9_-]+)="([^"]*)"/g;
      let m;
      while ((m = attrRegex.exec(line)) !== null) {
        currentInfo.attrs[m[1].toLowerCase()] = m[2];
      }
    } else if (!line.startsWith("#") && currentInfo) {
      currentInfo.url = line;
      items.push(currentInfo);
      currentInfo = null;
    }
  }

  return items;
}

function analyzeEntries(name, items) {
  const total = items.length;
  let countTvgId = 0;
  let countGroupTitle = 0;
  let countCountry = 0;
  let countLanguage = 0;
  let countLogo = 0;

  const urlFormats = { hls: 0, mp4: 0, youtube: 0, mpegts: 0, other: 0 };
  let overlapTvgId = 0;
  let overlapUrl = 0;

  const seenIds = new Set();
  const seenUrls = new Set();

  for (const item of items) {
    const attrs = item.attrs || {};
    const tvgId = (attrs["tvg-id"] || attrs["id"] || "").trim().toLowerCase();
    const group = (attrs["group-title"] || attrs["group"] || "").trim();
    const country = (attrs["tvg-country"] || attrs["country"] || "").trim();
    const lang = (attrs["tvg-language"] || attrs["language"] || "").trim();
    const logo = (attrs["tvg-logo"] || attrs["logo"] || "").trim();

    if (tvgId) countTvgId++;
    if (group) countGroupTitle++;
    if (country) countCountry++;
    if (lang) countLanguage++;
    if (logo) countLogo++;

    const rawUrl = (item.url || "").toLowerCase();
    if (rawUrl.includes("youtube.com") || rawUrl.includes("youtu.be")) {
      urlFormats.youtube++;
    } else if (rawUrl.includes(".m3u8") || rawUrl.includes("/hls/")) {
      urlFormats.hls++;
    } else if (rawUrl.includes(".mp4")) {
      urlFormats.mp4++;
    } else if (rawUrl.includes(".ts")) {
      urlFormats.mpegts++;
    } else {
      urlFormats.other++;
    }

    if (tvgId && iptvIdSet.has(tvgId)) {
      overlapTvgId++;
    }

    const normUrl = normalizeStreamUrl(item.url);
    if (normUrl && iptvUrlSet.has(normUrl)) {
      overlapUrl++;
    }
  }

  return {
    name,
    total,
    attributes: {
      tvgId: `${countTvgId} (${((countTvgId / total) * 100).toFixed(1)}%)`,
      groupTitle: `${countGroupTitle} (${((countGroupTitle / total) * 100).toFixed(1)}%)`,
      country: `${countCountry} (${((countCountry / total) * 100).toFixed(1)}%)`,
      language: `${countLanguage} (${((countLanguage / total) * 100).toFixed(1)}%)`,
      logo: `${countLogo} (${((countLogo / total) * 100).toFixed(1)}%)`
    },
    urlFormats,
    overlap: {
      byTvgId: `${overlapTvgId} (${((overlapTvgId / total) * 100).toFixed(1)}%)`,
      byUrl: `${overlapUrl} (${((overlapUrl / total) * 100).toFixed(1)}%)`
    }
  };
}

async function run() {
  const reports = [];

  for (const src of SOURCES) {
    const res = await fetchSource(src);
    if (res.error) {
      reports.push({ name: src.name, error: res.error, status: res.status });
      continue;
    }
    const items = parseM3uSimple(res.text);
    const analysis = analyzeEntries(src.name, items);
    reports.push(analysis);
  }

  console.log("\n=== REPORTE COMPARATIVO DE FUENTES MULTI-NIVEL ===");
  console.log(JSON.stringify(reports, null, 2));

  fs.writeFileSync("docs/faseA_sources_analysis.json", JSON.stringify(reports, null, 2), "utf8");
}

run();

