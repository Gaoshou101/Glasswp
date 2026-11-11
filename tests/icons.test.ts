import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { providerBrand, providerLogoPaths, weatherIconMarkup, weatherKind } from '../src/icons.ts';
import { weatherLabel } from '../src/format.ts';

test('weather codes map to drawable kinds without changing the text label', () => {
  const cases: [number | null, string][] = [[0, 'clear'], [1, 'partly'], [2, 'partly'], [3, 'cloudy'], [45, 'fog'], [48, 'fog'], [51, 'drizzle'], [57, 'drizzle'],
    [61, 'rain'], [67, 'rain'], [80, 'rain'], [82, 'rain'], [71, 'snow'], [77, 'snow'], [85, 'snow'], [86, 'snow'], [95, 'thunder'], [99, 'thunder'], [null, 'unknown'], [NaN, 'unknown'], [42, 'unknown']];
  for (const [code, kind] of cases) assert.equal(weatherKind(code), kind, String(code));
  for (const code of [-1, 0.5, Infinity]) assert.equal(weatherKind(code), 'unknown');
  // Text labels keep their existing wording; drizzle is still described as rain.
  assert.equal(weatherLabel(53), '雨'); assert.equal(weatherLabel(2), '多云');
});

test('weather icons draw day and night variants and keep mask IDs unique per copy', () => {
  assert.match(weatherIconMarkup(0, true), /wi-sun/); assert.match(weatherIconMarkup(0, false), /wi-moon/);
  assert.match(weatherIconMarkup(63), /wi-drop/); assert.match(weatherIconMarkup(95), /wi-bolt/); assert.match(weatherIconMarkup(73), /wi-snow/);
  assert.match(weatherIconMarkup(null), /wi-cloud-muted/);
  const ids = [weatherIconMarkup(2, true), weatherIconMarkup(2, false), weatherIconMarkup(3)].map(markup => /mask id="([^"]+)"/.exec(markup)?.[1]);
  assert.ok(ids.every(Boolean)); assert.equal(new Set(ids).size, ids.length);
  for (const markup of [weatherIconMarkup(2), weatherIconMarkup(3)]) assert.ok(markup.includes(`url(#${/mask id="([^"]+)"/.exec(markup)![1]})`));
});

test('official provider icons are local packaged assets', () => {
  for (const provider of Object.keys(providerLogoPaths)) {
    const path = providerLogoPaths[provider];
    assert.match(path, /^\.\/assets\/providers\/[a-z0-9-]+\.(svg|png|webp|jpg)$/);
    const bytes = readFileSync(new URL(`../public/${path.slice(2)}`, import.meta.url));
    assert.ok(bytes.length > 100, provider);
    if (path.endsWith('.svg')) {
      assert.match(bytes.toString(), /<svg\b/);
      assert.doesNotMatch(bytes.toString(), /<script\b|<foreignObject\b|\son\w+=|(?:href|url\()\s*=?\s*["']?https?:/i);
    }
  }
  assert.equal(providerLogoPaths['unknown-provider'], undefined);
});

test('all documented native and Magpie provider IDs resolve to packaged artwork', () => {
  const documentation = readFileSync(new URL('../docs/AI-SOURCES-2026-11-10.md', import.meta.url), 'utf8');
  const ids = new Set(['codex', 'claude', 'copilot', 'gemini', 'antigravity']);
  for (const match of documentation.matchAll(/^\|[^\n]+\/ `([^`]+)` \|/gm)) {
    for (const id of match[1].split(',').map(value => value.trim())) ids.add(id);
  }
  assert.ok(ids.size >= 69);
  for (const id of ids) assert.ok(providerLogoPaths[providerBrand(id)], `Unmapped provider: ${id}`);
  assert.equal(providerBrand(' GitHub-Copilot '), 'copilot');
  assert.equal(providerBrand('__proto__'), '__proto__');
  assert.equal(providerBrand('unknown-provider'), 'unknown-provider');
});

test('provenance manifest matches every shipped brand and exact asset bytes', () => {
  const manifest = JSON.parse(readFileSync(new URL('../public/assets/providers/manifest.json', import.meta.url), 'utf8'));
  assert.deepEqual(manifest.map((entry: { brand: string }) => entry.brand).sort(), Object.keys(providerLogoPaths).sort());
  for (const entry of manifest) {
    assert.equal(providerLogoPaths[entry.brand], `./assets/providers/${entry.file}`);
    assert.match(entry.source, /^https:\/\//);
    const bytes = readFileSync(new URL(`../public/assets/providers/${entry.file}`, import.meta.url));
    assert.equal(createHash('sha256').update(bytes).digest('hex'), entry.sha256, entry.brand);
    if (entry.resolution === 'vector') assert.doesNotMatch(bytes.toString(), /data:image\//);
    else assert.ok(Math.min(...entry.dimensions) >= 66, `${entry.brand}: at least 3x the 22px display size`);
  }
});
