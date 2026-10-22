export type Group = 'top' | 'quota' | 'hardware';
export interface Material { transparency: number; blur: number; edge: number }
export interface Widget { enabled: boolean; scale: number; x: number; y: number }
export interface Settings {
  version: 1; material: Record<Group, Material>; widgets: Record<Group, Widget>; bridgeUrl: string;
  readToken: string; timezone: string; seconds: boolean; hour12: boolean; motion: boolean; trends: boolean;
  rotateSeconds: number; quotaLayout: 'carousel' | 'cards'; ignoreCodexBaseWeekly: boolean; ignoreCodexImageQuota: boolean; safeBottom: number; resolution: string;
  materialMode: 'liquid' | 'frosted';
}
export const groups: Group[] = ['top', 'quota', 'hardware'];
export const defaultMaterial: Material = { transparency: 75, blur: 34, edge: 50 };
export const clamp = (value: unknown, min: number, max: number, fallback: number) => typeof value === 'number' && Number.isFinite(value) ? Math.max(min, Math.min(max, value)) : fallback;
export function defaults(): Settings {
  return { version: 1, material: Object.fromEntries(groups.map(g => [g, { ...defaultMaterial }])) as Settings['material'],
    widgets: Object.fromEntries(groups.map(g => [g, { enabled: true, scale: 1, x: 0, y: 0 }])) as Settings['widgets'],
    bridgeUrl: 'http://127.0.0.1:17632', readToken: '', timezone: 'Asia/Shanghai', seconds: false, hour12: false,
    motion: true, trends: true, rotateSeconds: 30, quotaLayout: 'cards', ignoreCodexBaseWeekly: false, ignoreCodexImageQuota: false, safeBottom: 3.9, resolution: '1600x2560', materialMode: 'liquid' };
}
export function loopbackUrl(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  try { const u = new URL(value); return u.protocol === 'http:' && ['127.0.0.1', 'localhost', '[::1]'].includes(u.hostname) && !u.username && !u.password && u.pathname === '/' && !u.search && !u.hash ? u.origin : null; } catch { return null; }
}
export function loadSettings(): Settings {
  const settings = defaults();
  try {
    const saved = JSON.parse(localStorage.getItem('wallpaper.dashboard.v1') || 'null');
    const legacy = JSON.parse(localStorage.getItem('wallpaper.frosted-glass.v2') || 'null');
    for (const group of groups) {
      const material = saved?.material?.[group] || legacy?.groups?.[group];
      settings.material[group] = { transparency: clamp(material?.transparency, 0, 100, 75), blur: clamp(material?.blur, 0, 64, 34), edge: clamp(material?.edge, 0, 100, 50) };
      const w = saved?.widgets?.[group];
      settings.widgets[group] = { enabled: w?.enabled !== false, scale: clamp(w?.scale, .8, 1.2, 1), x: clamp(w?.x, -5, 5, 0), y: clamp(w?.y, -5, 5, 0) };
    }
    if (saved) {
      settings.bridgeUrl = loopbackUrl(saved.bridgeUrl) || settings.bridgeUrl;
      settings.readToken = typeof saved.readToken === 'string' ? saved.readToken.slice(0, 128) : '';
      try { new Intl.DateTimeFormat('zh-CN', { timeZone: saved.timezone }); settings.timezone = saved.timezone || settings.timezone; } catch { /* Keep default timezone. */ }
      for (const key of ['seconds', 'hour12', 'motion', 'trends', 'ignoreCodexBaseWeekly', 'ignoreCodexImageQuota'] as const) if (typeof saved[key] === 'boolean') settings[key] = saved[key];
      settings.rotateSeconds = clamp(saved.rotateSeconds, 0, 300, 30);
      if (saved.quotaLayout === 'carousel' || saved.quotaLayout === 'cards') settings.quotaLayout = saved.quotaLayout;
      if (saved.materialMode === 'frosted' || saved.materialMode === 'liquid') settings.materialMode = saved.materialMode;
      settings.safeBottom = clamp(saved.safeBottom, 1, 10, 3.9);
      if (saved.resolution === 'viewport' || /^\d{3,4}x\d{3,4}$/.test(saved.resolution)) settings.resolution = saved.resolution;
    }
  } catch { /* Damaged or unavailable storage falls back to the accepted design. */ }
  return settings;
}
export function saveSettings(settings: Settings): boolean {
  try { localStorage.setItem('wallpaper.dashboard.v1', JSON.stringify(settings)); return true; } catch { return false; }
}
