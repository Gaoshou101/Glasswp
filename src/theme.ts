import { applyTheme, backgroundUrl, normalizeTheme, PRESETS, themeVariables } from '../public/theme.js';
export { applyTheme, backgroundUrl, normalizeTheme, PRESETS, themeVariables };
export type { Theme, ThemePreset, ThemeSurface, ThemePresetDefinition } from '../public/theme.js';

/** Apply the shared palette and background to a wallpaper root and its art layer. */
export function applyWallpaperTheme(root: HTMLElement, input?: Parameters<typeof normalizeTheme>[0]) {
  const theme = applyTheme(root, input);
  const art = root.querySelector<HTMLImageElement>('.art');
  if (art) art.src = backgroundUrl(theme, document.baseURI);
  return theme;
}
