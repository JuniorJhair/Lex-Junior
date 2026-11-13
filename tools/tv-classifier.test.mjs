import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { classifyFailure, pickEngineKind, buildHlsConfig } = require('../LexBrowser/Assets/tv/tv.js');

// 1. Offline / HTTP 404 & 410
{
  const r404 = classifyFailure({ status: 404 });
  assert.equal(r404.category, 'offline');
  assert.equal(r404.persistKind, 'dead');

  const r410 = classifyFailure({ status: 410 });
  assert.equal(r410.category, 'offline');
  assert.equal(r410.persistKind, 'dead');
}

// 2. Geo / Auth restricted (403, 401, 451)
{
  const r403 = classifyFailure({ status: 403 });
  assert.equal(r403.category, 'restricted');
  assert.equal(r403.persistKind, 'restricted');

  const r401 = classifyFailure({ status: 401 });
  assert.equal(r401.category, 'restricted');
  assert.equal(r401.persistKind, 'restricted');
}

// 3. Proxy 502/504 with typed X-Lex-Error-Class (tls, dns, timeout, blocked)
{
  const tls = classifyFailure({ status: 502, errorClass: 'tls' });
  assert.equal(tls.category, 'tls');
  assert.equal(tls.persistKind, 'dead');

  const dns = classifyFailure({ status: 502, errorClass: 'dns' });
  assert.equal(dns.category, 'dns');
  assert.equal(dns.persistKind, 'dead');

  const timeout = classifyFailure({ status: 502, errorClass: 'timeout' });
  assert.equal(timeout.category, 'timeout');
  assert.equal(timeout.persistKind, 'transient');

  const timeout504 = classifyFailure({ status: 504 });
  assert.equal(timeout504.category, 'timeout');
  assert.equal(timeout504.persistKind, 'transient');

  const blocked = classifyFailure({ status: 502, errorClass: 'blocked' });
  assert.equal(blocked.category, 'blocked');
  assert.equal(blocked.persistKind, 'restricted');
}

// 4. Codec errors (MSE bufferAddCodecError / MediaError 3 & 4)
{
  const c1 = classifyFailure({ details: 'bufferAddCodecError', codecs: { ac3: false } });
  assert.equal(c1.category, 'codec');
  assert.equal(c1.persistKind, 'codec');
  assert.match(c1.detail, /AC-3\/HEVC/);

  const c2 = classifyFailure({ mediaErrorCode: 3 });
  assert.equal(c2.category, 'codec');
  assert.equal(c2.persistKind, 'codec');
}

// 5. Parsing / HTML portal instead of manifest
{
  const p1 = classifyFailure({ details: 'manifestParsingError' });
  assert.equal(p1.category, 'parsing');
  assert.equal(p1.persistKind, 'dead');

  const p2 = classifyFailure({ status: 200, kind: 'html' });
  assert.equal(p2.category, 'parsing');
  assert.equal(p2.persistKind, 'dead');
}

// 6. Rate limiting 429 / 503 transient + stale gen
{
  const t = classifyFailure({ status: 429 });
  assert.equal(t.category, 'transient');
  assert.equal(t.persistKind, 'transient');
  assert.equal(t.retry, true);

  const stale = classifyFailure({ status: 410, errorClass: 'stale' });
  assert.equal(stale.category, 'stale');
  assert.equal(stale.title, '');
  assert.equal(stale.detail, '');
  assert.notEqual(stale.persistKind, 'dead');
  assert.equal(stale.retry, false);
}

// 7. Engine selection + low-mem hls config
{
  assert.equal(pickEngineKind('https://cdn.example.com/live/master.m3u8?token=abc', ''), 'hls');
  assert.equal(pickEngineKind('http://cdn.example.com/stream/123', 'mpegts'), 'mpegts');
  assert.equal(pickEngineKind('http://cdn.example.com/stream/123', ''), 'probe');
  assert.equal(pickEngineKind('https://cdn.example.com/video.mp4', ''), 'native');

  const cfg = buildHlsConfig({ version: '1.7.4' });
  assert.equal(cfg.maxBufferLength, 12);
  assert.equal(cfg.backBufferLength, 10);
  assert.equal(cfg.enableWorker, false);
  assert.ok(cfg.manifestLoadPolicy?.default?.errorRetry);
  assert.equal(cfg.manifestLoadPolicy.default.errorRetry.shouldRetry(null, null, { response: { code: 404 } }), false);
  assert.equal(cfg.manifestLoadPolicy.default.errorRetry.shouldRetry(null, null, { response: { code: 500 } }), true);
}

// 8. Encrypted stream rejected
{
  const enc = classifyFailure({ status: 422, errorClass: 'encrypted' });
  assert.equal(enc.category, 'restricted');
  assert.equal(enc.persistKind, 'restricted');
  assert.match(enc.detail, /cifrado/);
}

console.log('tv-classifier.test.mjs: ALL TESTS PASSED');

