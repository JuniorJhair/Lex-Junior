import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { convertSrtToVtt, formatTime } = require('../LexBrowser/Assets/tv/vod.js');

// 1. formatTime tests
{
  assert.equal(formatTime(0), '00:00');
  assert.equal(formatTime(5), '00:05');
  assert.equal(formatTime(65), '01:05');
  assert.equal(formatTime(3665), '1:01:05');
  assert.equal(formatTime(-10), '00:00');
  assert.equal(formatTime(NaN), '00:00');
}

// 2. convertSrtToVtt tests
{
  const srt = `1\r\n00:01:20,000 --> 00:01:24,400\r\nHola mundo!\r\n\r\n2\r\n00:01:25,123 --> 00:01:28,999\r\nSegunda línea`;
  const vtt = convertSrtToVtt(srt);

  assert.ok(vtt.startsWith('WEBVTT\n\n'));
  assert.ok(vtt.includes('00:01:20.000 --> 00:01:24.400'));
  assert.ok(vtt.includes('00:01:25.123 --> 00:01:28.999'));
  assert.ok(vtt.includes('Hola mundo!'));
  assert.ok(vtt.includes('Segunda línea'));
  assert.ok(!vtt.includes('\r\n'));
}

console.log('vod.test.mjs: ALL TESTS PASSED');

