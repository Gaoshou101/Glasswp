import { PRESETS, applyTheme, backgroundUrl, normalizeTheme } from '../theme.js';

const $ = id => document.getElementById(id);
const storageKey = 'wallpaper.admin.theme.v1';
const defaultAppearance = () => normalizeTheme({ preset: 'original', surface: 'dark' });
let savedAppearance = defaultAppearance();
let draftAppearance = defaultAppearance();
let adminMode = localStorage.getItem(storageKey) || 'follow';
let savedAdminMode = adminMode;
let session = '';
let saving = false;

function readAdminMode() {
  const value = localStorage.getItem(storageKey);
  return value === 'light' || value === 'dark' || value === 'follow' ? value : 'follow';
}

function currentDraft() {
  return normalizeTheme({
    background: draftAppearance.background,
    preset: $('wallpaper-theme-preset').value,
    surface: $('wallpaper-theme-surface').value,
    accent: $('wallpaper-theme-accent').value
  });
}

function setAdminTheme(mode, appearance = draftAppearance) {
  adminMode = mode === 'light' || mode === 'dark' ? mode : 'follow';
  const effective = adminMode === 'follow' ? appearance.surface : adminMode;
  applyTheme(document.documentElement, { ...appearance, surface: effective });
  document.documentElement.style.setProperty('--admin-background', `url("${backgroundUrl(appearance, location.origin + '/')}")`);
  document.documentElement.dataset.adminTheme = effective;
  document.documentElement.dataset.adminThemeMode = adminMode;
  $('admin-theme-mode').value = adminMode;
}

function dirty() {
  return JSON.stringify(currentDraft()) !== JSON.stringify(savedAppearance) || adminMode !== savedAdminMode;
}

function syncDirty() {
  const changed = dirty();
  $('save-appearance').disabled = saving || !changed;
  $('cancel-appearance').disabled = saving || !changed;
  document.querySelectorAll('[data-theme-dirty]').forEach(node => { node.hidden = !changed; });
  $('appearance-section').dataset.dirty = String(changed);
}

function renderPreview() {
  const preset = PRESETS[draftAppearance.preset] || PRESETS.original;
  $('wallpaper-theme-preset').value = draftAppearance.preset;
  $('wallpaper-theme-surface').value = draftAppearance.surface;
  $('wallpaper-theme-accent').value = draftAppearance.accent;
  $('wallpaper-theme-accent-value').textContent = draftAppearance.accent;
  $('appearance-preview-title').textContent = preset.label;
  $('appearance-preview-copy').textContent = `${draftAppearance.surface === 'light' ? '浅色' : '深色'}玻璃 · 管理页${adminMode === 'follow' ? '跟随壁纸' : adminMode === 'light' ? 'Light 浅色' : 'Dark 深色'}`;
  $('appearance-preview').style.setProperty('--accent', draftAppearance.accent);
  applyTheme(document.documentElement, draftAppearance);
  setAdminTheme(adminMode, draftAppearance);
  syncDirty();
}

async function setupApi(path, body = {}, retry = true) {
  const response = await fetch('/v1/setup/' + path, {
    method: 'POST', cache: 'no-store', headers: { 'Content-Type': 'application/json', 'X-Wallpaper-Setup': '1', 'X-Wallpaper-Admin': session }, body: JSON.stringify(body)
  });
  const result = await response.json().catch(() => ({}));
  if (response.status === 403 && path !== 'session' && retry) { session = (await setupApi('session', {}, false)).session; return setupApi(path, body, false); }
  if (!response.ok) throw new Error(result.message || '主题设置读取失败');
  return result;
}

async function read() {
  try {
    session = (await setupApi('session')).session;
    const config = await setupApi('config/read');
    savedAppearance = normalizeTheme(config.appearance || {});
    draftAppearance = { ...savedAppearance };
    renderPreview();
    $('appearance-status').textContent = '主题设置已读取。';
  } catch (error) {
    draftAppearance = defaultAppearance(); savedAppearance = { ...draftAppearance }; renderPreview();
    $('appearance-status').textContent = `无法读取主题设置：${error.message}，可先调整本地预览。`;
  }
}

async function save() {
  if (saving || !dirty()) return;
  saving = true; syncDirty();
  const submitted = currentDraft(), submittedMode = adminMode;
  try {
    const result = await setupApi('config', { appearance: submitted });
    savedAppearance = { ...submitted };
    savedAdminMode = submittedMode;
    localStorage.setItem(storageKey, submittedMode);
    $('appearance-status').textContent = result.message || '主题设置已保存，壁纸刷新后生效。';
  } catch (error) {
    $('appearance-status').textContent = `保存失败：${error.message}`;
  } finally { saving = false; renderPreview(); }
}

function cancel() {
  draftAppearance = { ...savedAppearance };
  adminMode = savedAdminMode;
  renderPreview();
  $('appearance-status').textContent = '已撤销未保存主题。';
}

document.addEventListener('DOMContentLoaded', () => {
  adminMode = readAdminMode(); savedAdminMode = adminMode;
  $('wallpaper-theme-preset').replaceChildren(...Object.values(PRESETS).map(preset => {
    const option = document.createElement('option'); option.value = preset.preset; option.textContent = preset.label; return option;
  }));
  $('wallpaper-theme-preset').addEventListener('change', () => { const preset = PRESETS[$('wallpaper-theme-preset').value] || PRESETS.original; draftAppearance = normalizeTheme(preset); renderPreview(); });
  $('wallpaper-theme-surface').addEventListener('change', () => { draftAppearance = normalizeTheme({ ...draftAppearance, surface: $('wallpaper-theme-surface').value }); renderPreview(); });
  $('wallpaper-theme-accent').addEventListener('input', () => { draftAppearance = normalizeTheme({ ...draftAppearance, accent: $('wallpaper-theme-accent').value }); renderPreview(); });
  $('admin-theme-mode').addEventListener('change', () => { adminMode = $('admin-theme-mode').value; renderPreview(); });
  $('save-appearance').addEventListener('click', save);
  $('cancel-appearance').addEventListener('click', cancel);
  renderPreview();
  void read();
});
addEventListener('beforeunload', event => { if (dirty()) { event.preventDefault(); event.returnValue = ''; } });
