export type ThemePreset = 'original' | 'neon' | 'pool';
export type ThemeSurface = 'light' | 'dark';
export interface Theme { preset: ThemePreset; accent: string; surface: ThemeSurface; background: string }
export interface ThemePresetDefinition extends Theme { label: string }
export declare const PRESETS: Readonly<Record<ThemePreset, ThemePresetDefinition>>;
export declare function normalizeTheme(input?: Partial<Theme> | null): Theme;
export declare function themeVariables(input?: Partial<Theme> | null): Record<string, string>;
export declare function applyTheme(element: HTMLElement | null, input?: Partial<Theme> | null): Theme;
export declare function backgroundUrl(input?: Partial<Theme> | null, baseUrl?: string): string;
