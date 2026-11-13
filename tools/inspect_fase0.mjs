import fs from 'node:fs';
import https from 'node:https';

async function fetchBuffer(url) {
  return new Promise((resolve, reject) => {
    https.get(url, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        return fetchBuffer(res.headers.location).then(resolve, reject);
      }
      if (res.statusCode !== 200) {
        return reject(new Error(`HTTP ${res.statusCode} for ${url}`));
      }
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => resolve(Buffer.concat(chunks)));
    }).on('error', reject);
  });
}

async function probeUrl(url) {
  return new Promise((resolve) => {
    try {
      const u = new URL(url);
      const req = https.request({
        method: 'GET',
        hostname: u.hostname,
        path: u.pathname + u.search,
        headers: {
          'Range': 'bytes=0-1023',
          'User-Agent': 'Mozilla/5.0 LexBrowser/2.0'
        }
      }, (res) => {
        const redirected = res.statusCode >= 300 && res.statusCode < 400;
        const location = res.headers.location || '';
        const status = res.statusCode;
        const ct = res.headers['content-type'] || '';
        const cr = res.headers['content-range'] || '';
        const ar = res.headers['accept-ranges'] || '';
        const cl = res.headers['content-length'] || '';
        res.destroy();
        resolve({ url, status, redirected, location, ct, cr, ar, cl });
      });
      req.on('error', err => resolve({ url, error: err.message }));
      req.setTimeout(12000, () => { req.destroy(); resolve({ url, error: 'timeout' }); });
      req.end();
    } catch (e) {
      resolve({ url, error: e.message });
    }
  });
}

async function checkHead(url) {
  return new Promise((resolve) => {
    try {
      const u = new URL(url);
      const req = https.request({
        method: 'HEAD',
        hostname: u.hostname,
        path: u.pathname + u.search,
        headers: { 'User-Agent': 'Mozilla/5.0' }
      }, (res) => {
        resolve({ status: res.statusCode, headers: res.headers });
      });
      req.on('error', err => resolve({ error: err.message }));
      req.end();
    } catch (e) {
      resolve({ error: e.message });
    }
  });
}

async function main() {
  console.log('Downloading catalog.json...');
  const catBuf = await fetchBuffer('https://biskyt.github.io/legalFreeIPTVplaylist/catalog.json');
  console.log(`catalog.json size: ${(catBuf.length / (1024 * 1024)).toFixed(2)} MB (${catBuf.length} bytes)`);

  console.log('Downloading report.json...');
  let reportData = null;
  try {
    const reportBuf = await fetchBuffer('https://biskyt.github.io/legalFreeIPTVplaylist/report.json');
    reportData = JSON.parse(reportBuf.toString('utf8'));
    console.log(`report.json size: ${(reportBuf.length / 1024).toFixed(2)} KB`);
  } catch (e) {
    console.log('report.json error:', e.message);
  }

  console.log('Downloading vod.m3u...');
  const vodBuf = await fetchBuffer('https://biskyt.github.io/legalFreeIPTVplaylist/vod.m3u');
  const vodLines = vodBuf.toString('utf8').split('\n');
  console.log(`vod.m3u lines: ${vodLines.length}, size: ${(vodBuf.length / (1024 * 1024)).toFixed(2)} MB`);

  // Check language files
  const langChecks = ['spa.m3u', 'es.m3u', 'spanish.m3u'];
  const langResults = {};
  for (const l of langChecks) {
    const head = await checkHead(`https://biskyt.github.io/legalFreeIPTVplaylist/languages/${l}`);
    langResults[l] = head;
  }

  // Parse catalog
  const catalog = JSON.parse(catBuf.toString('utf8'));
  console.log(`Catalog type: ${Array.isArray(catalog) ? 'Array' : typeof catalog}`);
  const items = Array.isArray(catalog) ? catalog : (catalog.items || catalog.titles || catalog.entries || []);
  console.log(`Total catalog items: ${items.length}`);

  // Analyze fields
  const fieldTypes = {};
  const sampleValues = {};
  const kinds = new Set();
  const languages = new Map();
  const licenses = new Map();
  const sources = new Map();
  const hosts = new Map();
  let moviesCount = 0;
  let seriesCount = 0;
  let episodeCount = 0;
  const seriesMap = new Map();

  const archiveUrls = [];
  const wikimediaUrls = [];

  for (const it of items) {
    for (const [k, v] of Object.entries(it)) {
      if (!fieldTypes[k]) {
        fieldTypes[k] = new Set();
        sampleValues[k] = v;
      }
      fieldTypes[k].add(Array.isArray(v) ? 'array' : typeof v);
    }

    const kind = it.kind || it.type || it['tvg-type'] || '';
    kinds.add(kind);

    if (kind === 'series' || it.series || it['series-name'] || it.season != null || it.episode != null) {
      episodeCount++;
      const sName = it.series || it['series-name'] || it.title;
      seriesMap.set(sName, (seriesMap.get(sName) || 0) + 1);
    } else {
      moviesCount++;
    }

    const lang = it.language || it.languages || it.lang || '';
    const langList = Array.isArray(lang) ? lang : [lang];
    for (const l of langList) {
      if (l) languages.set(l, (languages.get(l) || 0) + 1);
    }

    const lic = it.license || it.licence || '';
    if (lic) licenses.set(lic, (licenses.get(lic) || 0) + 1);

    const src = it.source || it.source_name || it.sourceName || '';
    if (src) sources.set(src, (sources.get(src) || 0) + 1);

    const mediaUrl = it.media_url || it.mediaUrl || it.url || it.stream_url || '';
    if (mediaUrl) {
      try {
        const u = new URL(mediaUrl);
        hosts.set(u.hostname, (hosts.get(u.hostname) || 0) + 1);
        if (u.hostname.includes('archive.org') && archiveUrls.length < 10) archiveUrls.push(mediaUrl);
        if (u.hostname.includes('wikimedia.org') && wikimediaUrls.length < 10) wikimediaUrls.push(mediaUrl);
      } catch {}
    }
  }

  seriesCount = seriesMap.size;

  // Probe archive.org and wikimedia URLs
  console.log('\nProbing archive.org URLs...');
  const probeArchive = [];
  for (const u of archiveUrls.slice(0, 5)) {
    probeArchive.push(await probeUrl(u));
  }

  console.log('\nProbing wikimedia URLs...');
  const probeWiki = [];
  for (const u of wikimediaUrls.slice(0, 3)) {
    probeWiki.push(await probeUrl(u));
  }

  // Count Spanish titles
  let spanishCount = 0;
  for (const it of items) {
    const l = JSON.stringify(it.language || it.languages || it.lang || '').toLowerCase();
    if (l.includes('spa') || l.includes('es') || l.includes('spanish') || l.includes('español')) {
      spanishCount++;
    }
  }

  // Also check if languages/spa.m3u exists and how many lines
  let spaM3uCount = 0;
  try {
    const spaBuf = await fetchBuffer('https://biskyt.github.io/legalFreeIPTVplaylist/languages/spa.m3u');
    const spaLines = spaBuf.toString('utf8').split('\n').filter(l => l.startsWith('#EXTINF:'));
    spaM3uCount = spaLines.length;
  } catch (e) {
    spaM3uCount = `Error: ${e.message}`;
  }

  const analysis = {
    catalogSizeMb: (catBuf.length / (1024 * 1024)).toFixed(2),
    totalItems: items.length,
    moviesCount,
    seriesCount,
    episodeCount,
    fieldTypes: Object.fromEntries(Object.entries(fieldTypes).map(([k, s]) => [k, Array.from(s)])),
    sampleValues,
    kinds: Array.from(kinds),
    topLanguages: Array.from(languages.entries()).sort((a,b) => b[1] - a[1]).slice(0, 20),
    topLicenses: Array.from(licenses.entries()).sort((a,b) => b[1] - a[1]).slice(0, 20),
    topSources: Array.from(sources.entries()).sort((a,b) => b[1] - a[1]).slice(0, 20),
    topHosts: Array.from(hosts.entries()).sort((a,b) => b[1] - a[1]).slice(0, 20),
    spanishCountInCatalog: spanishCount,
    languagesSpaM3uEntries: spaM3uCount,
    langHeadResults: langResults,
    vodM3uFirst20Lines: vodLines.slice(0, 20),
    probeArchive,
    probeWiki,
    reportSummary: reportData ? {
      total: reportData.total,
      valid: reportData.valid,
      rejected: reportData.rejected,
      stats: reportData.stats
    } : null
  };

  fs.mkdirSync('docs', { recursive: true });
  fs.writeFileSync('docs/cine-analysis.json', JSON.stringify(analysis, null, 2));
  console.log('\nSaved analysis to docs/cine-analysis.json');
}

main().catch(err => {
  console.error('Fatal error:', err);
  process.exit(1);
});

