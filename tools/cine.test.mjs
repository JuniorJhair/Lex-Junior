// tools/cine.test.mjs
// Suite de tests unitarios para la lógica pura de Lex TV Cine (VOD)
import assert from "node:assert/strict";

function normalizeText(text) {
  if (!text) return "";
  return text.toString()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase()
    .trim();
}

function formatTime(seconds) {
  if (!seconds || isNaN(seconds) || seconds < 0) return "0:00";
  const total = Math.floor(seconds);
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m >= 60) {
    const h = Math.floor(m / 60);
    const remM = m % 60;
    return `${h}:${remM.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}`;
  }
  return `${m}:${s.toString().padStart(2, "0")}`;
}

function calculateProgress(positionSec, durationSec) {
  if (!durationSec || durationSec <= 0) return 0;
  return Math.min(100.0, Math.round((positionSec / durationSec) * 1000) / 10);
}

function filterItems(items, filter, userState) {
  const q = normalizeText(filter.query);
  const kind = filter.kind || "all";
  const genre = filter.genre || "all";
  const decade = filter.decade || "all";
  const lang = filter.lang || "all";
  const wikiOnly = !!filter.wikimediaOnly;
  const hideUnav = filter.hideUnavailable !== false;

  return items.filter(item => {
    if (hideUnav && userState && userState.unavailable && userState.unavailable[item.id]) {
      return false;
    }
    if (wikiOnly && !item.mediaUrl.includes("upload.wikimedia.org")) {
      return false;
    }
    if (kind !== "all" && item.kind !== kind) {
      return false;
    }
    if (genre !== "all") {
      if (!item.genres || !item.genres.some(g => g.toLowerCase() === genre.toLowerCase())) {
        return false;
      }
    }
    if (lang !== "all") {
      if (!item.languages || !item.languages.some(l => l.toLowerCase() === lang.toLowerCase())) {
        return false;
      }
    }
    if (decade !== "all" && item.year > 0) {
      const y = item.year;
      if (decade === "1900s" && (y < 1900 || y > 1909)) return false;
      if (decade === "1910s" && (y < 1910 || y > 1919)) return false;
      if (decade === "1920s" && (y < 1920 || y > 1929)) return false;
      if (decade === "1930s" && (y < 1930 || y > 1939)) return false;
      if (decade === "1940s" && (y < 1940 || y > 1949)) return false;
      if (decade === "1950s" && (y < 1950 || y > 1959)) return false;
      if (decade === "1960s" && (y < 1960 || y > 1969)) return false;
      if (decade === "1970s" && y < 1970) return false;
    }
    if (q.length > 0) {
      const normTitle = normalizeText(item.title);
      if (normTitle.includes(q)) return true;
      if (item.genres && item.genres.some(g => normalizeText(g).includes(q))) return true;
      return false;
    }
    return true;
  });
}

// --- TESTS ---

// 1. Normalización sin acentos y case-insensitive
{
  assert.equal(normalizeText("El Hotel Eléctrico"), "el hotel electrico");
  assert.equal(normalizeText("Película de Acción & Aventura"), "pelicula de accion & aventura");
  assert.equal(normalizeText("   MÜSICA Clásica  "), "musica clasica");
  assert.equal(normalizeText(""), "");
  assert.equal(normalizeText(null), "");
}

// 2. Formato de tiempo (mm:ss o hh:mm:ss)
{
  assert.equal(formatTime(0), "0:00");
  assert.equal(formatTime(45), "0:45");
  assert.equal(formatTime(65), "1:05");
  assert.equal(formatTime(3599), "59:59");
  assert.equal(formatTime(3600), "1:00:00");
  assert.equal(formatTime(3665), "1:01:05");
  assert.equal(formatTime(-10), "0:00");
}

// 3. Cálculo de porcentaje de progreso
{
  assert.equal(calculateProgress(0, 100), 0);
  assert.equal(calculateProgress(50, 100), 50);
  assert.equal(calculateProgress(92, 100), 92);
  assert.equal(calculateProgress(99, 100), 99);
  assert.equal(calculateProgress(150, 100), 100);
  assert.equal(calculateProgress(10, 0), 0);
}

// 4. Filtrado combinable sobre fixture
{
  const fixture = [
    {
      id: "ia.ElHotelElectrico",
      title: "El Hotel Eléctrico",
      kind: "movie",
      year: 1908,
      genres: ["Ciencia Ficción", "Fantasía"],
      languages: ["Español"],
      mediaUrl: "https://archive.org/download/hotel/hotel.mp4"
    },
    {
      id: "ia.NightOfTheLivingDead",
      title: "Night of the Living Dead",
      kind: "movie",
      year: 1968,
      genres: ["Terror"],
      languages: ["English"],
      mediaUrl: "https://archive.org/download/notld/notld.mp4"
    },
    {
      id: "wiki.BigBuckBunny",
      title: "Big Buck Bunny",
      kind: "movie",
      year: 2008,
      genres: ["Animación", "Comedia"],
      languages: ["English"],
      mediaUrl: "https://upload.wikimedia.org/wikipedia/commons/bbb.webm"
    },
    {
      id: "wiki.SherlockHolmesSeries",
      title: "The Adventures of Sherlock Holmes",
      kind: "series",
      year: 1954,
      genres: ["Misterio", "Drama"],
      languages: ["English"],
      mediaUrl: "https://upload.wikimedia.org/wikipedia/commons/sherlock.webm"
    }
  ];

  const userState = {
    unavailable: {
      "ia.NightOfTheLivingDead": { kind: "dead", reason: "404" }
    }
  };

  // Test: Búsqueda con acento/sin acento
  let res = filterItems(fixture, { query: "electrico" }, userState);
  assert.equal(res.length, 1);
  assert.equal(res[0].id, "ia.ElHotelElectrico");

  res = filterItems(fixture, { query: "eléctrico" }, userState);
  assert.equal(res.length, 1);
  assert.equal(res[0].id, "ia.ElHotelElectrico");

  // Test: Ocultar caídos (elimina NightOfTheLivingDead)
  res = filterItems(fixture, { hideUnavailable: true }, userState);
  assert.equal(res.length, 3);
  assert.ok(!res.some(x => x.id === "ia.NightOfTheLivingDead"));

  // Test: Mostrar caídos
  res = filterItems(fixture, { hideUnavailable: false }, userState);
  assert.equal(res.length, 4);

  // Test: Solo Wikimedia
  res = filterItems(fixture, { wikimediaOnly: true }, userState);
  assert.equal(res.length, 2);
  assert.ok(res.every(x => x.mediaUrl.includes("upload.wikimedia.org")));

  // Test: Filtrar por tipo serie
  res = filterItems(fixture, { kind: "series" }, userState);
  assert.equal(res.length, 1);
  assert.equal(res[0].id, "wiki.SherlockHolmesSeries");

  // Test: Filtrar por década 1900s
  res = filterItems(fixture, { decade: "1900s" }, userState);
  assert.equal(res.length, 1);
  assert.equal(res[0].id, "ia.ElHotelElectrico");

  // Test: Filtrar por género Fantasía
  res = filterItems(fixture, { genre: "Fantasía" }, userState);
  assert.equal(res.length, 1);
  assert.equal(res[0].id, "ia.ElHotelElectrico");
}

// 5. Heurística de detección de Anime
function scoreAnime(ch) {
  if (!ch) return 0;
  let score = 0;
  const name = (ch.name || "").toLowerCase();
  const group = (ch.group || "").toLowerCase();
  const lang = (ch.language || "").toLowerCase();
  const tags = (ch.tags || "").toLowerCase();

  const animeKeywords = ["anime", "manga", "otaku", "toei", "crunchyroll", "animax", "funimation", "retro anime", "aniverse"];
  for (const kw of animeKeywords) {
    if (name.includes(kw)) score += 5;
    if (group.includes(kw) || tags.includes(kw)) score += 3;
  }

  if (lang === "jpn" || lang === "ja" || (ch.country || "").toLowerCase() === "jp") {
    if (group.includes("animat") || group.includes("animac") || group.includes("kids")) score += 4;
    else score += 2;
  }

  const franchises = [
    "dragon ball", "naruto", "one piece", "pokemon", "pokémon", "digimon",
    "sailor moon", "doraemon", "shin chan", "yugioh", "yu-gi-oh", "bleach",
    "inuyasha", "ranma", "saint seiya", "caballeros del zodiaco", "capitan tsubasa",
    "supercampeones", "death note", "evangelion", "ghibli", "conan"
  ];
  for (const f of franchises) {
    if (name.includes(f)) score += 6;
  }

  return score;
}

{
  assert.ok(scoreAnime({ name: "Toei Animation Channel", group: "Animation" }) >= 5);
  assert.ok(scoreAnime({ name: "Dragon Ball Z Latino", group: "Animación" }) >= 6);
  assert.ok(scoreAnime({ name: "Crunchyroll TV", language: "English" }) >= 5);
  assert.ok(scoreAnime({ name: "Tokyo MX", language: "jpn", group: "Animation", country: "jp" }) >= 4);
  assert.equal(scoreAnime({ name: "BBC News", group: "News", language: "English" }), 0);
  assert.equal(scoreAnime({ name: "Cartoon Network", group: "Animation", language: "Spanish" }), 0);
}

// 6. Formateo y clasificación de Watch Providers en Perú
function formatWatchProviders(providers) {
  if (!Array.isArray(providers)) return { flatrate: [], rent: [], buy: [] };
  return {
    flatrate: providers.filter(p => p.type === "flatrate"),
    rent: providers.filter(p => p.type === "rent"),
    buy: providers.filter(p => p.type === "buy")
  };
}

{
  const provs = [
    { name: "Disney Plus", type: "flatrate" },
    { name: "Star Plus", type: "flatrate" },
    { name: "Google TV", type: "rent" },
    { name: "Apple TV", type: "buy" }
  ];
  const classified = formatWatchProviders(provs);
  assert.equal(classified.flatrate.length, 2);
  assert.equal(classified.rent.length, 1);
  assert.equal(classified.buy.length, 1);
  assert.equal(classified.flatrate[0].name, "Disney Plus");
}

console.log("cine.test.mjs: ALL TESTS PASSED");

