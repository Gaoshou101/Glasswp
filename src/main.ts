import './live.css';
import './liquid.css';
import './polish.css';
import './clock.css';
import './theme.css';
import { LiquidInteraction } from './liquid';
import { CardDetails } from './card-details';
import { authorizeDesktopInput, installDesktopInput } from './desktop-input';
import type { Snapshot } from './contract';
import { ClockWeather, HardwareComponent, QuotaComponent } from './components';
import { QuotaRefreshControl } from './quota-refresh';
import { CurrentRequest, SnapshotChanges } from './snapshot-control';
declare const __BUILD_ID__: string;
import { applyClockConfig, validClockConfig, clamp, defaultMaterial, groups, loadSettings, loopbackUrl, saveSettings, type Group, type Material } from './settings';
import { applyWallpaperTheme, normalizeTheme, packageTheme } from './theme';

declare global {
  interface Window {
    wallpaperPropertyListener: { applyUserProperties(properties: Record<string, { value: unknown }>): void; applyGeneralProperties(properties: { fps?: number }): void; setPaused(paused: boolean): void };
  }
}
const settings = loadSettings();
const pinnedTheme = packageTheme(document.documentElement.dataset.wallpaperPreset);
if (pinnedTheme) settings.theme = pinnedTheme;
const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
const root = document.querySelector<HTMLElement>('.wallpaper')!;
applyWallpaperTheme(root, settings.theme);
const fullView = !new URLSearchParams(location.search).has('studio');
if (fullView) document.documentElement.setAttribute('data-capture', '');
const clock = new ClockWeather(root, settings), quota = new QuotaComponent(root, settings), hardware = new HardwareComponent(root, settings);
const liquid = new LiquidInteraction(root), details = new CardDetails(root, () => settings.bridgeUrl, () => settings.readToken);
let paused = false, engine = false, fps = 30, lastFetch = 0, lastSuccess = 0, connected = false, errors = 0;
const requests = new CurrentRequest(), changes = new SnapshotChanges();
const desktopInput = installDesktopInput(root, () => engine, point => authorizeDesktopInput(settings.bridgeUrl, settings.readToken, point), () => !paused);
let tickTimer = 0, ambientTimer = 0, reportedAt = 0;
let lastSnapshot: Snapshot | undefined;
let managedClock = false;
const quotaRefresh = new QuotaRefreshControl($<HTMLButtonElement>('refresh-quota'), () => settings, () => { lastFetch = 0; void poll(); });
const nodes: Record<Group, HTMLElement> = { top: root.querySelector('.top')!, quota: root.querySelector('.quota')!, hardware: root.querySelector('.hardware')! };
const target = $<HTMLSelectElement>('glass-target');
const resolution = $<HTMLSelectElement>('screen-resolution');
const presets: Record<string, Material> = { liquid: { transparency: 83, blur: 26, edge: 68 }, clear: { transparency: 87, blur: 17, edge: 36 }, soft: defaultMaterial, deep: { transparency: 54, blur: 48, edge: 63 } };
const selected = (): Group[] => target.value === 'all' ? groups : [target.value as Group];
const store = () => { $('settings-status').textContent = saveSettings(settings) ? '已保存浏览器预览设置，不会同步到桌面壁纸。' : '本次预览调节有效；当前浏览器无法保存。'; };

function applyAppearance() {
  root.dataset.materialMode = settings.materialMode;
  $<HTMLAnchorElement>('weather-settings').href = settings.bridgeUrl + '/admin/#city-query';
  for (const group of groups) {
    const n = nodes[group], m = settings.material[group], w = settings.widgets[group], alpha = (100 - m.transparency) / 100, edge = m.edge / 100;
    const values = { '--glass-alpha': alpha, '--glass-blur': m.blur, '--glass-edge': edge, '--glass-light': alpha * .26 + edge * .04, '--glass-violet': alpha * .14 + edge * .018, '--glass-grain': .028 + m.blur / 64 * .024 };
    for (const [key, value] of Object.entries(values)) { n.style.setProperty(key, String(value)); n.querySelectorAll<HTMLElement>('.glass').forEach(panel => panel.style.setProperty(key, String(value))); }
    n.classList.toggle('widget-hidden', !w.enabled);
    n.style.transform = `translate(${w.x * root.clientWidth / 100}px, ${w.y * root.clientHeight / 100}px) scale(${w.scale})`;
    n.style.transformOrigin = group === 'top' ? 'top center' : 'bottom center';
  }
  root.style.setProperty('--safe-bottom', `${settings.safeBottom}%`);
  root.dataset.quotaLayout = settings.quotaLayout;
  // Keep quota anchored above the active bottom component, preserving the face gap.
  nodes.quota.style.bottom = settings.widgets.hardware.enabled ? '' : 'var(--safe-bottom)';
  clock.tick();
  liquid.invalidate(); details.invalidatePlacement();
  if (lastSnapshot) renderSnapshot(lastSnapshot);
  details.sync();
}
function renderSnapshot(data: Snapshot) {
  if (data.appearance && !pinnedTheme) {
    const nextTheme = normalizeTheme(data.appearance);
    if (JSON.stringify(nextTheme) !== JSON.stringify(settings.theme)) { settings.theme = applyWallpaperTheme(root, nextTheme); saveSettings(settings); }
  }
  // Clock settings have no weather/hardware revision: apply them before source filtering.
  const wasManaged = managedClock; managedClock = validClockConfig(data.clock);
  const clockChanged = applyClockConfig(settings, data.clock);
  if (clockChanged || wasManaged !== managedClock) { controls(); clock.tick(); if (clockChanged) saveSettings(settings); }
  const appearance = JSON.stringify([settings.timezone, settings.trends, settings.quotaLayout, settings.ignoreCodexBaseWeekly, settings.ignoreCodexImageQuota]);
  const changed = changes.changed(data, appearance);
  if (changed.weather) clock.update(data.weather);
  if (changed.quota) quota.update(data.quota);
  if (changed.hardware) hardware.update(data.hardware);
}
function controls() {
  const group = selected()[0], m = settings.material[group], w = settings.widgets[group];
  for (const [key, id, out] of [['transparency', 'glass-transparency', 'transparency-value'], ['blur', 'glass-blur', 'blur-value'], ['edge', 'glass-edge', 'edge-value']] as const) {
    $<HTMLInputElement>(id).value = String(m[key]); $(out).textContent = m[key] + (key === 'blur' ? ' px' : '%');
  }
  $<HTMLInputElement>('widget-enabled').checked = w.enabled; $<HTMLInputElement>('widget-scale').value = String(w.scale * 100); $<HTMLInputElement>('widget-x').value = String(w.x); $<HTMLInputElement>('widget-y').value = String(w.y);
  for (const key of ['seconds', 'hour12', 'motion', 'trends'] as const) $<HTMLInputElement>({ seconds: 'show-seconds', hour12: 'hour12', motion: 'motion', trends: 'trends' }[key]).checked = settings[key];
  $<HTMLInputElement>('timezone').value = settings.timezone; $<HTMLInputElement>('safe-bottom').value = String(settings.safeBottom); $<HTMLInputElement>('rotate-seconds').value = String(settings.rotateSeconds);
  $<HTMLSelectElement>('quota-layout').value = settings.quotaLayout;
  $<HTMLSelectElement>('material-mode').value = settings.materialMode;
  $<HTMLInputElement>('ignore-codex-base-weekly').checked = settings.ignoreCodexBaseWeekly;
  $<HTMLInputElement>('ignore-codex-image-quota').checked = settings.ignoreCodexImageQuota;
  $<HTMLInputElement>('bridge-url').value = settings.bridgeUrl; $<HTMLInputElement>('read-token').value = settings.readToken;
  $<HTMLAnchorElement>('admin-link').href = settings.bridgeUrl + '/admin/';
  $<HTMLAnchorElement>('clock-admin-link').href = settings.bridgeUrl + '/admin/#clock-heading';
  for (const id of ['timezone', 'show-seconds', 'hour12']) $<HTMLInputElement>(id).disabled = managedClock;
  $('clock-config-status').textContent = managedClock ? '时间由管理页统一设置，保存后自动同步到壁纸。' : '当前使用本地时间设置；可在管理页设置时区和校准。';
  document.querySelectorAll<HTMLButtonElement>('[data-preset]').forEach(b => b.setAttribute('aria-pressed', String(selected().every(g => (Object.keys(defaultMaterial) as (keyof Material)[]).every(k => settings.material[g][k] === presets[b.dataset.preset!][k])))));
}
for (const [key, id] of [['transparency', 'glass-transparency'], ['blur', 'glass-blur'], ['edge', 'glass-edge']] as const) {
  $(id).addEventListener('input', () => { selected().forEach(g => settings.material[g][key] = Number($<HTMLInputElement>(id).value)); applyAppearance(); controls(); }); $(id).addEventListener('change', store);
}
target.addEventListener('change', controls);
document.querySelectorAll<HTMLButtonElement>('[data-preset]').forEach(b => b.onclick = () => { selected().forEach(g => settings.material[g] = { ...presets[b.dataset.preset!] }); if (b.dataset.preset === 'liquid') settings.materialMode = 'liquid'; applyAppearance(); controls(); lifecycle(); store(); });
$('material-mode').addEventListener('change', () => { settings.materialMode = $<HTMLSelectElement>('material-mode').value === 'frosted' ? 'frosted' : 'liquid'; applyAppearance(); lifecycle(); store(); });
$('reset-glass').onclick = () => { selected().forEach(g => settings.material[g] = { ...defaultMaterial }); applyAppearance(); controls(); store(); };
for (const [key, id] of [['enabled', 'widget-enabled'], ['scale', 'widget-scale'], ['x', 'widget-x'], ['y', 'widget-y']] as const) {
  $(id).addEventListener('input', () => { const input = $<HTMLInputElement>(id); for (const group of selected()) { if (key === 'enabled') settings.widgets[group].enabled = input.checked; else settings.widgets[group][key] = Number(input.value) / (key === 'scale' ? 100 : 1); } applyAppearance(); store(); });
}
for (const [key, id] of [['seconds', 'show-seconds'], ['hour12', 'hour12'], ['motion', 'motion'], ['trends', 'trends']] as const) $(id).addEventListener('change', () => { settings[key] = $<HTMLInputElement>(id).checked; applyAppearance(); lifecycle(); store(); });
$('timezone').addEventListener('change', () => { const tz = $<HTMLInputElement>('timezone').value; try { new Intl.DateTimeFormat('zh-CN', { timeZone: tz }); settings.timezone = tz; clock.tick(); store(); } catch { $('settings-status').textContent = '请输入有效时区，例如 Asia/Shanghai。'; } });
$('safe-bottom').addEventListener('change', () => { settings.safeBottom = clamp(Number($<HTMLInputElement>('safe-bottom').value), 1, 10, 3.9); applyAppearance(); store(); });
$('rotate-seconds').addEventListener('change', () => { settings.rotateSeconds = clamp(Number($<HTMLInputElement>('rotate-seconds').value), 0, 300, 0); store(); });
$('quota-layout').addEventListener('change', () => { settings.quotaLayout = $<HTMLSelectElement>('quota-layout').value === 'carousel' ? 'carousel' : 'cards'; quota.refreshLayout(); applyAppearance(); store(); });
$('ignore-codex-base-weekly').addEventListener('change', () => { settings.ignoreCodexBaseWeekly = $<HTMLInputElement>('ignore-codex-base-weekly').checked; quota.refreshLayout(); store(); });
$('ignore-codex-image-quota').addEventListener('change', () => { settings.ignoreCodexImageQuota = $<HTMLInputElement>('ignore-codex-image-quota').checked; quota.refreshLayout(); store(); });
$('save-connection').onclick = () => {
  const url = loopbackUrl($<HTMLInputElement>('bridge-url').value);
  if (!url) { $('connection-status').textContent = '本机数据地址需使用 http://127.0.0.1:端口。'; return; }
  settings.bridgeUrl = url; settings.readToken = $<HTMLInputElement>('read-token').value.trim(); managedClock = false; lastSnapshot = undefined; requests.cancel(); changes.reset(); lastSuccess = lastFetch = 0; connected = false; errors = 0; controls(); store(); void poll();
};

const shell = document.querySelector<HTMLElement>('.preview-shell')!;
if ([...resolution.options].some(o => o.value === settings.resolution)) resolution.value = settings.resolution;
function fit() {
  if (!fullView) {
    const [width, height] = resolution.value === 'viewport' ? innerWidth < innerHeight ? [innerWidth, innerHeight] : [1600, 2560] : resolution.value.split('x').map(Number);
    const availableWidth = Math.max(1, Math.min(shell.clientWidth, innerWidth <= 760 ? 420 : Infinity)), availableHeight = Math.max(1, innerHeight - 108);
    const scale = Math.min(availableWidth / width, availableHeight / height);
    root.style.setProperty('--canvas-width', `${width * scale}px`); root.style.setProperty('--canvas-height', `${height * scale}px`);
    $('resolution-status').textContent = `${width} × ${height} · 背景等比填充，模块自动适配。`;
  }
  const width = root.clientWidth, height = root.clientHeight, compact = 1 - Math.max(0, Math.min(1, (width / height - .625) / .125)) * .12;
  root.style.setProperty('--u', `${Math.min(width / 1080, height / 1920) * compact}px`); root.dataset.canvasSize = `${width}x${height}`; applyAppearance();
}
resolution.addEventListener('change', () => { settings.resolution = resolution.value; fit(); store(); });
new ResizeObserver(fit).observe(shell); new ResizeObserver(() => { fit(); }).observe(root); addEventListener('resize', fit);
const ambient = document.createElement('div'); ambient.className = 'ambient'; root.prepend(ambient);
const reduced = matchMedia('(prefers-reduced-motion: reduce)');
function updateNote() {
  const note = root.querySelector('.note')!;
  if (!settings.readToken) note.textContent = '请在设置中连接本机数据';
  else if (!connected) note.textContent = lastSuccess ? '本机桥接已断开 · 显示上次数据' : '正在连接本机数据';
  else if (!lastSuccess || Date.now() - lastSuccess > 10000) note.textContent = '数据已过期 · 显示上次数据';
  else note.textContent = '';
  note.toggleAttribute('hidden', !note.textContent);
  hardware.stale();
}
async function report() {
  if (!settings.readToken || !connected || Date.now() - reportedAt < 5000) return;
  reportedAt = Date.now();
    try { await fetch(settings.bridgeUrl + '/v1/client', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Wallpaper-Token': settings.readToken }, body: JSON.stringify({ buildId: typeof __BUILD_ID__ === 'undefined' ? 'development' : __BUILD_ID__, wallpaperEngine: engine, width: root.clientWidth, height: root.clientHeight, fps, paused, materials: Object.fromEntries(groups.map(g => [g, [settings.material[g].transparency, settings.material[g].blur, settings.material[g].edge]])) }), cache: 'no-store' }); } catch { }
}
async function poll() {
  if (paused || requests.pending || !settings.readToken) return;
  const now = Date.now();
  if (now - lastFetch < Math.min(30000, 2000 * 2 ** Math.min(errors, 4))) return;
  lastFetch = now; const controller = requests.begin();
  const timeout = setTimeout(() => controller.abort(), 8000);
  try {
    const response = await fetch(settings.bridgeUrl + '/v1/snapshot', { headers: { 'X-Wallpaper-Token': settings.readToken }, signal: controller.signal, cache: 'no-store' });
    if (!response.ok) throw new Error(response.status === 401 ? '展示票据无效，请从本机设置重新复制。' : '本机数据暂不可用。');
    const data: Snapshot = await response.json();
    if (!requests.current(controller) || paused) return;
    if (data.schemaVersion !== 1 || !data.hardware || !Array.isArray(data.quota?.accounts) || !data.weather) throw new Error('桥接数据版本不兼容。');
    lastSnapshot = data; lastSuccess = Date.now(); errors = 0; connected = true;
    renderSnapshot(data); details.sync(); $('connection-status').textContent = '本机数据已连接。';
    void report(); void quotaRefresh.sync();
  } catch (e) { if (requests.owns(controller) && !paused) { connected = false; errors++; $('connection-status').textContent = e instanceof Error && e.name !== 'AbortError' ? e.message : '连接超时，正在重试。'; } }
  finally { clearTimeout(timeout); if (requests.owns(controller)) { requests.finish(controller); updateNote(); } }
}
function lifecycle() {
  clearInterval(tickTimer); clearInterval(ambientTimer);
  root.dataset.paused = String(paused); ambient.hidden = !settings.motion || reduced.matches || settings.materialMode === 'liquid';
  liquid.configure(!paused && settings.motion && !reduced.matches && settings.materialMode === 'liquid', fps);
  if (root.dataset.dynamic !== 'true') root.getAnimations({ subtree: true }).forEach(animation => { if (!(animation instanceof CSSAnimation || animation instanceof CSSTransition)) animation.cancel(); });
  details.sync();
  if (paused) { desktopInput.cancel(); requests.cancel(); quotaRefresh.stop(); return; }
  clock.tick(); quota.tick(); updateNote();
  tickTimer = window.setInterval(() => { clock.tick(); quota.tick(); details.sync(); quotaRefresh.tick(); updateNote(); void poll(); }, 1000);
  if (!ambient.hidden) ambientTimer = window.setInterval(() => { const phase = Date.now() / 18000; ambient.style.opacity = String(.45 + Math.sin(phase) * .35); ambient.style.transform = `translate(${Math.sin(phase) * 1.5}%, ${Math.cos(phase) * 1.2}%)`; }, Math.max(100, 1000 / Math.max(1, fps)));
  lastFetch = 0; void poll();
}
reduced.addEventListener('change', lifecycle);
document.addEventListener('visibilitychange', () => { if (!engine) { paused = document.hidden; lifecycle(); } });
addEventListener('pageshow', () => { lastFetch = 0; lifecycle(); });
window.wallpaperPropertyListener = {
  applyUserProperties(properties) {
    engine = true;
    const oldConnection = settings.bridgeUrl + ':' + settings.readToken;
    for (const group of groups) {
      for (const [name, key, max] of [['transparency', 'transparency', 100], ['blur', 'blur', 64], ['highlight', 'edge', 100]] as const) {
        const value = properties[`${group}_glass_${name}`] ?? properties[`glass_${name}`];
        if (value !== undefined) settings.material[group][key] = clamp(Number(value.value), 0, max, defaultMaterial[key]);
      }
      if (properties[`${group}_enabled`]) settings.widgets[group].enabled = properties[`${group}_enabled`].value === true;
      if (properties[`${group}_scale`]) settings.widgets[group].scale = clamp(Number(properties[`${group}_scale`].value) / 100, .8, 1.2, 1);
      for (const axis of ['x', 'y'] as const) if (properties[`${group}_${axis}`]) settings.widgets[group][axis] = clamp(Number(properties[`${group}_${axis}`].value), -5, 5, 0);
    }
    if (properties.bridge_url) settings.bridgeUrl = loopbackUrl(properties.bridge_url.value) || settings.bridgeUrl;
    if (properties.bridge_token) settings.readToken = String(properties.bridge_token.value || '').trim().slice(0, 128);
    const connectionChanged = oldConnection !== settings.bridgeUrl + ':' + settings.readToken;
    if (connectionChanged) { managedClock = false; lastSnapshot = undefined; requests.cancel(); changes.reset(); }
    if (properties.material_mode) settings.materialMode = properties.material_mode.value === 'frosted' ? 'frosted' : 'liquid';
    for (const key of ['seconds', 'hour12', 'motion', 'trends'] as const) if (properties[key] && (!managedClock || key === 'motion' || key === 'trends')) settings[key] = properties[key].value === true;
    if (properties.rotate_seconds) settings.rotateSeconds = clamp(Number(properties.rotate_seconds.value), 0, 300, 0);
    if (properties.quota_layout) { settings.quotaLayout = properties.quota_layout.value === 'carousel' ? 'carousel' : 'cards'; quota.refreshLayout(); }
    if (properties.ignore_codex_base_weekly) { settings.ignoreCodexBaseWeekly = properties.ignore_codex_base_weekly.value === true; quota.refreshLayout(); }
    if (properties.ignore_codex_image_quota) { settings.ignoreCodexImageQuota = properties.ignore_codex_image_quota.value === true; quota.refreshLayout(); }
    if (properties.safe_bottom) settings.safeBottom = clamp(Number(properties.safe_bottom.value), 1, 10, 3.9);
    if (properties.timezone && !managedClock) try { new Intl.DateTimeFormat('zh-CN', { timeZone: String(properties.timezone.value) }); settings.timezone = String(properties.timezone.value); } catch { }
    applyAppearance(); controls(); saveSettings(settings); lastFetch = reportedAt = 0; lifecycle();
  },
  applyGeneralProperties(properties) { engine = true; if (properties.fps) fps = clamp(properties.fps, 1, 240, 30); lifecycle(); },
  setPaused(value) { engine = true; paused = value; reportedAt = 0; lifecycle(); void report(); }
};
fit(); controls(); lifecycle();
