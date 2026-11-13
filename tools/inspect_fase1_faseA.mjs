// tools/inspect_fase1_faseA.mjs
import fs from "node:fs";
import path from "node:path";
import http from "node:http";
import https from "node:https";

const localAppData = process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE, "AppData", "Local");
const catalogPath = path.join(localAppData, "LexBrowser", "tv_catalog_cache.json");
const prefsPath = path.join(localAppData, "LexBrowser", "tv_prefs.json");

console.log("=== FASE 1: AUDITORÍA DE CANALES DE ANIMACIÓN / ANIME EN IPTV-ORG ===");

if (!fs.existsSync(catalogPath)) {
  console.error("No se encontró tv_catalog_cache.json en", catalogPath);
  process.exit(1);
}

const catalogRaw = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
const channels = catalogRaw.channels || catalogRaw.Channels || [];
console.log(`Total canales en catálogo local: ${channels.length}`);

let prefs = { deadStreams: {} };
if (fs.existsSync(prefsPath)) {
  try {
    prefs = JSON.parse(fs.readFileSync(prefsPath, "utf8"));
  } catch {}
}
const deadMap = prefs.deadStreams || {};
console.log(`Total URLs marcadas caídas en prefs: ${Object.keys(deadMap).length}`);

// Filtrar canales de animación
// Categorías posibles: "animation", "Animación", etc.
const animChannels = channels.filter(c => {
  const g = (c.group || c.Group || "").toLowerCase();
  return g === "animation" || g === "animación" || g.includes("animat") || g.includes("anime");
});

console.log(`Canales con categoría animation: ${animChannels.length}`);

// Desglose por idioma
const langBreakdown = {};
for (const c of animChannels) {
  const l = (c.language || c.Language || "und").toLowerCase();
  langBreakdown[l] = (langBreakdown[l] || 0) + 1;
}

// Desglose por país
const countryBreakdown = {};
for (const c of animChannels) {
  const co = (c.country || c.Country || "INT").toUpperCase();
  countryBreakdown[co] = (countryBreakdown[co] || 0) + 1;
}

// Canales caídos
let deadCount = 0;
for (const c of animChannels) {
  const u = c.url || c.Url || "";
  if (deadMap[u]) deadCount++;
}

console.log("\n--- Desglose por idioma (Animación) ---");
const sortedLangs = Object.entries(langBreakdown).sort((a, b) => b[1] - a[1]);
for (const [l, cnt] of sortedLangs) {
  console.log(`  ${l}: ${cnt}`);
}

console.log("\n--- Top 15 Países (Animación) ---");
const sortedCountries = Object.entries(countryBreakdown).sort((a, b) => b[1] - a[1]).slice(0, 15);
for (const [co, cnt] of sortedCountries) {
  console.log(`  ${co}: ${cnt}`);
}

console.log(`\nCanales de animación marcados caídos en prefs: ${deadCount} de ${animChannels.length}`);

// Heurística de Anime probable
// Criterios:
// 1. Nombre contiene "anime", "manga", "otaku", "animax", "crunchyroll", "toei", "chibi", "aniverse", "gundam", "pokemon", "naruto", "dragon ball", "inuyasha", "yugioh", "retro anime" (+10 puntos)
// 2. Idioma es "jpn", "ja", "japanese" (+5 puntos)
// 3. País es "JP" (+3 puntos)
// 4. Tags / Id contienen "anime" (+4 puntos)
const animeKeywords = [
  "anime", "manga", "otaku", "animax", "crunchyroll", "toei", "chibi",
  "aniverse", "gundam", "pokemon", "pokémon", "naruto", "dragon ball",
  "inuyasha", "yu-gi-oh", "yugioh", "retro anime", "anime tv", "anitube",
  "aniplus", "animix", "doraemon", "shin chan", "conan", "sailor moon"
];

function scoreAnime(c) {
  const name = (c.name || c.Name || "").toLowerCase();
  const id = (c.id || c.Id || "").toLowerCase();
  const lang = (c.language || c.Language || "").toLowerCase();
  const country = (c.country || c.Country || "").toUpperCase();
  const tags = (c.tags || c.Tags || "").toLowerCase();

  let score = 0;
  let matches = [];

  for (const kw of animeKeywords) {
    if (name.includes(kw)) {
      score += 10;
      matches.push(`nombre:${kw}`);
    }
  }

  if (id.includes("anime") || id.includes("animax")) {
    score += 5;
    matches.push("id:anime");
  }

  if (lang === "jpn" || lang === "ja" || lang.includes("japan")) {
    score += 6;
    matches.push("idioma:jpn");
  }

  if (country === "JP") {
    score += 3;
    matches.push("pais:JP");
  }

  if (tags.includes("anime")) {
    score += 4;
    matches.push("tags:anime");
  }

  return { score, matches, channel: c };
}

const scoredChannels = animChannels
  .map(c => scoreAnime(c))
  .filter(x => x.score > 0)
  .sort((a, b) => b.score - a.score || (a.channel.name || "").localeCompare(b.channel.name || ""));

// También buscar en TODO el catálogo por si hay canales con anime en el nombre pero otra categoría (e.g. general/entertainment/kids)
const otherAnimeCandidates = channels
  .filter(c => {
    const g = (c.group || c.Group || "").toLowerCase();
    if (g === "animation" || g === "animación" || g.includes("animat")) return false;
    const name = (c.name || c.Name || "").toLowerCase();
    return animeKeywords.some(kw => name.includes(kw));
  })
  .map(c => scoreAnime(c))
  .filter(x => x.score >= 10);

const allAnimeScored = [...scoredChannels, ...otherAnimeCandidates].sort((a, b) => b.score - a.score);

// Tomar los 40 nombres con mayor puntuación
const top40Anime = [];
const seenNames = new Set();
for (const item of allAnimeScored) {
  const name = item.channel.name || item.channel.Name || "";
  if (!seenNames.has(name.toLowerCase())) {
    seenNames.add(name.toLowerCase());
    top40Anime.push(item);
    if (top40Anime.length >= 40) break;
  }
}

console.log("\n--- Top 40 Canales con mayor probabilidad de ser Anime ---");
top40Anime.forEach((item, idx) => {
  const c = item.channel;
  console.log(`${(idx + 1).toString().padStart(2, " ")}. ${c.name || c.Name} [${c.country || c.Country || "??"}|${c.language || c.Language || "??"}] (score ${item.score}, motivos: ${item.matches.join(", ")})`);
});

// Guardar resultados intermedios de Fase 1
const fase1Data = {
  totalAnimation: animChannels.length,
  deadAnimationCount: deadCount,
  languages: langBreakdown,
  countries: countryBreakdown,
  top40Anime: top40Anime.map(x => ({
    name: x.channel.name || x.channel.Name,
    id: x.channel.id || x.channel.Id,
    country: x.channel.country || x.channel.Country,
    language: x.channel.language || x.channel.Language,
    group: x.channel.group || x.channel.Group,
    url: x.channel.url || x.channel.Url,
    score: x.score,
    matches: x.matches
  }))
};

fs.writeFileSync("docs/fase1_animacion.json", JSON.stringify(fase1Data, null, 2), "utf8");
console.log("\nFase 1 guardada en docs/fase1_animacion.json");

