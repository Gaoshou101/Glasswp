import assert from 'node:assert/strict';
import test from 'node:test';
import { PRESETS, backgroundUrl, normalizeTheme, themeVariables } from '../public/theme.js';
import { packageTheme } from '../src/theme';
import { defaults, loadSettings } from '../src/settings';

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
  assert.equal(themeVariables({ preset: 'neon', surface: 'light' })['--on-accent'], '#10170d');
  assert.equal(themeVariables({ preset: 'pool', surface: 'dark' })['--on-accent'], '#ffffff');
});

test('backgroundUrl resolves relative to the package root', () => {
  assert.equal(backgroundUrl({ preset: 'neon' }, 'https://example.test/dist/index.html'), 'https://example.test/dist/assets/wallpapers/neon.jpg');
});

test('named Workshop packages select their own theme before reading bridge data', () => {
  for (const preset of ['original', 'neon', 'pool'] as const) {
    assert.deepEqual(packageTheme(preset), normalizeTheme({ preset }));
  }
  for (const value of [undefined, '', '__proto__', 'toString', 'unknown']) {
    assert.equal(packageTheme(value), null);
    assert.equal(normalizeTheme({ preset: value as any }).preset, 'original');
  }
});

test('saved generic wallpaper theme is restored without changing widget settings', () => {
  const prior = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
  const saved = { ...defaults(), theme: { ...normalizeTheme({ preset: 'pool' }), accent: '#123456' } };
  saved.widgets.top.x = 2;
  Object.defineProperty(globalThis, 'localStorage', { configurable: true, value: { getItem: (key: string) => key === 'wallpaper.dashboard.v1' ? JSON.stringify(saved) : null } });
  try {
    assert.deepEqual(loadSettings().theme, saved.theme);
    assert.equal(loadSettings().widgets.top.x, 2);
  } finally { if (prior) Object.defineProperty(globalThis, 'localStorage', prior); else Reflect.deleteProperty(globalThis, 'localStorage'); }
});
