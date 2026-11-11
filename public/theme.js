/**
 * Shared wallpaper theme contract.  This file is deliberately dependency-free so
 * the Wallpaper Engine bundle and the admin page can use the same palette rules.
 */
const PRESETS = Object.freeze({
  original: Object.freeze({ label: '原版紫色', preset: 'original', accent: '#dcc6ff', surface: 'dark', background: 'assets/wallpaper-original.jpg' }),
  neon: Object.freeze({ label: '霓虹黄绿', preset: 'neon', accent: '#e5f34d', surface: 'dark', background: 'assets/wallpapers/neon.jpg' }),
  pool: Object.freeze({ label: '泳池青蓝', preset: 'pool', accent: '#147f91', surface: 'light', background: 'assets/wallpapers/pool.jpg' }),
});

const HEX = /^#[0-9a-f]{6}$/i;
const hexToRgb = (hex) => {
  const value = hex.slice(1);
  return [parseInt(value.slice(0, 2), 16), parseInt(value.slice(2, 4), 16), parseInt(value.slice(4, 6), 16)];
};
const rgb = (values) => values.join(',');
const rgba = (values, alpha) => `rgba(${values[0]},${values[1]},${values[2]},${alpha})`;
const safeBackground = (value, fallback) => {
  if (typeof value !== 'string' || value.length > 240 || value.includes('..') || value.includes('\\') || /^(?:https?:|data:|javascript:)/i.test(value)) return fallback;
  return value.replace(/^\.?\//, '').replace(/^assets\//, 'assets/').match(/^assets\/[\w./-]+\.(?:jpe?g|png|webp)$/i) ? value.replace(/^\.?\//, '') : fallback;
};

export function normalizeTheme(input) {
  const raw = input && typeof input === 'object' ? input : {};
  const preset = typeof raw.preset === 'string' && PRESETS[raw.preset] ? raw.preset : 'original';
  const base = PRESETS[preset];
  const accent = typeof raw.accent === 'string' && HEX.test(raw.accent) ? raw.accent.toLowerCase() : base.accent;
  const surface = raw.surface === 'light' || raw.surface === 'dark' ? raw.surface : base.surface;
  return { preset, accent, surface, background: safeBackground(raw.background, base.background) };
}

export function themeVariables(input) {
  const theme = normalizeTheme(input);
  const accentRgb = hexToRgb(theme.accent);
  const light = theme.surface === 'light';
  const ink = light ? '#152b33' : '#fbf8ff';
  const ink2 = light ? 'rgba(21,43,51,.78)' : 'rgba(245,239,255,.8)';
  const ink3 = light ? 'rgba(21,43,51,.58)' : 'rgba(238,230,252,.62)';
  const page = light ? '#eaf7f8' : '#17151d';
  const surfaceRgb = light ? '245,252,252' : '17,12,28';
  return {
    '--ink': ink, '--ink-2': ink2, '--ink-3': ink3, '--muted': ink3,
    '--accent': theme.accent, '--accent-rgb': rgb(accentRgb), '--accent-soft': rgba(accentRgb, .2),
    '--accent-strong': rgba(accentRgb, .8), '--surface-rgb': surfaceRgb, '--page-bg': page,
    '--hairline': light ? 'rgba(21,65,72,.18)' : 'rgba(255,248,255,.12)',
    '--hairline-strong': light ? 'rgba(21,65,72,.3)' : 'rgba(255,248,255,.22)',
    '--field': light ? 'rgba(255,255,255,.72)' : 'rgba(12,9,20,.42)',
    '--field-hover': light ? 'rgba(255,255,255,.9)' : 'rgba(18,14,28,.52)',
    '--fill': light ? 'rgba(20,110,123,.1)' : 'rgba(255,255,255,.07)',
    '--well': light ? 'rgba(20,110,123,.06)' : 'rgba(8,6,14,.24)',
    '--on-accent': light ? '#fff' : '#10170d', '--warn': light ? '#a85d17' : '#ffd7a8',
    '--danger': light ? '#a32d35' : '#ffa3a3', '--ok': light ? '#16745b' : '#9fe3c0',
    '--theme-shadow': light ? 'rgba(21,72,78,.2)' : 'rgba(6,3,14,.36)',
  };
}

export function applyTheme(element, input) {
  const theme = normalizeTheme(input);
  if (!element) return theme;
  const variables = themeVariables(theme);
  for (const [name, value] of Object.entries(variables)) element.style.setProperty(name, value);
  element.dataset.theme = theme.preset;
  element.dataset.surface = theme.surface;
  return theme;
}

export function backgroundUrl(input, baseUrl = typeof location === 'undefined' ? 'http://localhost/' : location.href) {
  const theme = normalizeTheme(input);
  return new URL(theme.background, baseUrl).href;
}

export { PRESETS };
