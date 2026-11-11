import { applyTheme, backgroundUrl, normalizeTheme, PRESETS, themeVariables } from '../public/theme.js';
export { applyTheme, backgroundUrl, normalizeTheme, PRESETS, themeVariables };
export type { Theme, ThemePreset, ThemeSurface, ThemePresetDefinition } from '../public/theme.js';

/** Named Workshop packages keep their own artwork even with a shared bridge. */
export function packageTheme(preset: unknown) {
  return typeof preset === 'string' && Object.prototype.hasOwnProperty.call(PRESETS, preset)
    ? normalizeTheme({ preset: preset as keyof typeof PRESETS }) : null;
}

/** Apply the shared palette and background to a wallpaper root and its art layer. */
export function applyWallpaperTheme(root: HTMLElement, input?: Parameters<typeof normalizeTheme>[0]) {
  const theme = applyTheme(root, input);
  const art = root.querySelector<HTMLImageElement>('.art');
  const source = backgroundUrl(theme, document.baseURI);
  if (art && art.src !== source) art.src = source;
  return theme;
}
