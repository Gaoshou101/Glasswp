import assert from 'node:assert/strict';
import test from 'node:test';
import { PRESETS, backgroundUrl, normalizeTheme, themeVariables } from '../public/theme.js';

test('theme presets keep wallpaper assets inside the package', () => {
  assert.deepEqual(Object.keys(PRESETS), ['original', 'neon', 'pool']);
  for (const preset of Object.values(PRESETS)) {
    assert.match(preset.background, /^assets\/[\w./-]+\.(?:jpe?g|png|webp)$/);
    assert.match(preset.accent, /^#[0-9a-f]{6}$/i);
  }
});

test('normalizeTheme rejects unsafe custom values and keeps a valid preset', () => {
  assert.deepEqual(normalizeTheme({ preset: 'pool', accent: 'red', surface: 'sideways', background: '../secret.jpg' }), {
    preset: 'pool', accent: PRESETS.pool.accent, surface: PRESETS.pool.surface, background: PRESETS.pool.background,
  });
  assert.equal(normalizeTheme({ preset: 'neon', accent: '#AABBCC' }).accent, '#aabbcc');
});

test('themeVariables expose accent and surface tokens', () => {
  const vars = themeVariables({ preset: 'pool' });
  assert.equal(vars['--accent'], PRESETS.pool.accent);
  assert.equal(vars['--surface-rgb'], '245,252,252');
  assert.match(vars['--accent-rgb'], /^\d+,\d+,\d+$/);
});

test('backgroundUrl resolves relative to the package root', () => {
  assert.equal(backgroundUrl({ preset: 'neon' }, 'https://example.test/dist/index.html'), 'https://example.test/dist/assets/wallpapers/neon.jpg');
});
