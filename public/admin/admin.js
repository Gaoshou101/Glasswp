'use strict';
const $ = id => document.getElementById(id);
let session = '', config = null, cities = [], refreshBusy = false, sensorsLoaded = false;
// Account editor: config.accounts is the saved state; the draft keeps watch order and aliases until saved.
let discovered = null, draftWatched = [], draftAlias = new Map(), accountsSaving = false, accountNotice = '', drag = null;
let accountPlans = new Map(), plansUnavailable = false, plansReadAt = 0, plansLoading = false, plansGeneration = 0;
let diagnosticsReadAt = 0, unsavedKey = '';
let aiSourcesReadAt = 0, aiSourcesLoading = false, aiSourceVersion = 0;
const readerSources = ['local', 'magpie', 'cpa', 'sub2api'], remoteAiSources = ['cpa', 'sub2api'];
const aiDetected = Object.fromEntries(readerSources.map(s => [s, []])), aiSelections = Object.fromEntries(readerSources.map(s => [s, []]));
const aiOptionDefault = source => ({ autoRefreshEnabled: true, refreshMinutes: source === 'magpie' ? 1 : 10, accounts: null });
const savedAiOptions = source => ({ ...aiOptionDefault(source), ...savedAiSources()[source + 'Options'] });
const aiOptionsDraft = source => ({ autoRefreshEnabled: $(source + '-auto-refresh').checked, refreshMinutes: Number($(source + '-refresh-minutes').value), accounts: $(source + '-all-accounts').checked ? null : aiSelections[source].map(a => ({ id: a.id, alias: a.alias.trim() })) });
const busy = new Set();
const sensorFields = { 'gpu-id': 'gpuId', 'cpu-temp': 'cpuTemperatureId', 'gpu-temp': 'gpuTemperatureId' };
const providerNames = { codex: 'Codex', antigravity: 'Antigravity', claude: 'Claude', kimi: 'Kimi', xai: 'xAI', devin: 'Devin', meta: 'Meta' };
const providerName = provider => providerNames[provider] || provider || '未知平台';
const trimUrl = value => String(value || '').trim().replace(/\/+$/, '');
const clockDefault = { timezone: 'Asia/Shanghai', offsetSeconds: 0, seconds: false, hour12: false, worldZones: ['Asia/Shanghai', 'Asia/Tokyo', 'Asia/Singapore', 'Europe/London', 'America/New_York', 'America/Los_Angeles'] };
const savedClock = () => config?.clock || clockDefault;
const savedWeather = () => config?.weather || { provider: 'open-meteo', apiKeyConfigured: false };
const aiSourcesDefault = { source: 'cpamp', magpieUrl: 'http://127.0.0.1:3425', magpieKeyConfigured: false, cpaUrl: '', cpaKeyConfigured: false, cpaApiVersion: 'v0', sub2apiUrl: '', sub2apiKeyConfigured: false };
const aiSourceNames = { cpamp: 'CPAMP', local: '本机自动读取', magpie: 'Magpie', cpa: 'CPA', sub2api: 'Sub2API' };
const savedAiSources = () => ({ ...aiSourcesDefault, ...config?.aiSources });
const aiSourcesDraft = () => ({ source: $('ai-source').value, magpieUrl: trimUrl($('ai-magpie-url').value), ...Object.fromEntries(readerSources.map(source => [source + 'Options', aiOptionsDraft(source)])), ...Object.fromEntries(remoteAiSources.map(source => [source + 'Url', trimUrl($('ai-' + source + '-url').value)])), cpaApiVersion: $('ai-cpa-version').value });
const aiSourcesDirty = () => {
  const draft = aiSourcesDraft(), saved = savedAiSources();
  return draft.source !== saved.source || draft.magpieUrl !== trimUrl(saved.magpieUrl) ||
    readerSources.some(source => JSON.stringify(draft[source + 'Options']) !== JSON.stringify(savedAiOptions(source))) ||
    $('ai-magpie-key').value !== '' || $('ai-magpie-clear-key').checked || draft.cpaApiVersion !== saved.cpaApiVersion ||
    remoteAiSources.some(source => draft[source + 'Url'] !== trimUrl(saved[source + 'Url']) || $('ai-' + source + '-key').value !== '' || $('ai-' + source + '-clear-key').checked);
};
const worldZones = () => [...new Set($('clock-world-zones').value.split(/[\r\n,]+/).map(zone => zone.trim()).filter(Boolean))];
const clockDraft = () => ({ timezone: $('clock-timezone').value.trim(), offsetSeconds: $('clock-offset').value.trim() === '' ? null : Number($('clock-offset').value), seconds: $('clock-seconds').checked, hour12: $('clock-hour12').checked, worldZones: worldZones() });
const validTimezone = zone => { try { if (!zone) return false; new Intl.DateTimeFormat('zh-CN', { timeZone: zone }); return true; } catch { return false; } };
const clockDirty = () => {
  const draft = clockDraft(), saved = savedClock();
  return ['timezone', 'offsetSeconds', 'seconds', 'hour12'].some(key => draft[key] !== saved[key]) || JSON.stringify(draft.worldZones) !== JSON.stringify(saved.worldZones);
};
const weatherApiHost = () => $('weather-api-host').value.trim().toLowerCase();
const validWeatherApiHost = host => host.length <= 253 && host.endsWith('.qweatherapi.com') &&
  host.split('.').every(label => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label));
const weatherCityCode = () => $('weather-city-code').value.trim();
const weatherCanRetainKey = () => $('weather-provider').value === savedWeather().provider && savedWeather().apiKeyConfigured &&
  ($('weather-provider').value !== 'qweather' || weatherApiHost() === String(savedWeather().apiHost || '').trim().toLowerCase());
const weatherDirty = () => $('weather-provider').value !== savedWeather().provider || $('weather-api-key').value !== '' || $('weather-clear-key').checked ||
  ($('weather-provider').value === 'qweather' && weatherApiHost() !== (savedWeather().apiHost || '')) ||
  ($('weather-provider').value === 'amap' && weatherCityCode() !== (savedWeather().cityCode || ''));
// Tone drives the status dot: progress messages pulse, failures turn red, cancellations stay neutral.
function status(message, tone = /^正在/.test(message) ? 'busy' : /^已取消/.test(message) ? 'info' : 'ok') {
  $('status').textContent = message;
  $('status').closest('.dock').dataset.tone = tone;
}
function bridgeState(state, text) { const node = $('bridge-state'); node.dataset.state = state; node.textContent = text; }
const svgIcon = name => {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'), use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  svg.setAttribute('class', 'icon'); svg.setAttribute('aria-hidden', 'true'); use.setAttribute('href', '#i-' + name); svg.append(use);
  return svg;
};
async function api(path, body = {}, retry = true) {
  const response = await fetch('/v1/setup/' + path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Wallpaper-Setup': '1', 'X-Wallpaper-Admin': session }, body: JSON.stringify(body), cache: 'no-store' });
  const result = await response.json().catch(() => ({}));
  if (response.status === 403 && path !== 'session' && retry) { session = (await api('session', {}, false)).session; return api(path, body, false); }
  if (!response.ok) throw new Error(result.message || (response.status === 403 ? '设置会话已过期，请刷新此页' : '读取失败，请检查地址、鉴权和版本'));
  return result;
}
function action(id, fn) {
  $(id).addEventListener('click', async () => {
    if (busy.has(id)) return;
    busy.add(id); $(id).disabled = true;
    try { await fn(); } catch (error) { status(error.message, 'error'); }
    finally { busy.delete(id); $(id).disabled = false; syncControls(); }
  });
}
function syncControls() {
  const set = (id, disabled) => { $(id).disabled = busy.has(id) || disabled; };
  const accountsChanged = accountsDirty();
  // Save buttons are enabled only while their section differs from the saved configuration.
  set('refresh-quota-now', savedAiSources().source !== 'cpamp' || refreshBusy || !config?.managementConfigured || !config?.accounts.length);
  set('save-city', !config || !cityDirty());
  set('save-clock', !config || (Boolean(config.clock) && !clockDirty()));
  set('cancel-clock', !config || !clockDirty());
  set('sync-clock', !config);
  set('reset-clock-offset', !config || Number($('clock-offset').value) === 0);
  set('save-weather', !config || !weatherDirty());
  set('cancel-weather', !config || !weatherDirty());
  set('save-ai-sources', !config || !aiSourcesDirty());
  set('cancel-ai-sources', !config || !aiSourcesDirty());
  const savedAi = savedAiSources();
  set('refresh-ai-sources', !config || aiSourcesLoading || savedAi.source === 'cpamp');
  for (const source of readerSources) {
    set('save-' + source + '-settings', !config || !aiSourcesDirty());
    set('cancel-' + source + '-settings', !config || !aiSourcesDirty());
    set('refresh-' + source + '-settings', !config || aiSourcesLoading || savedAi.source !== source);
  }
  set('save-cpamp', !config || !cpampDirty());
  set('clear-cpamp', !config?.managementConfigured);
  set('save-quota-refresh', !config || !refreshDirty());
  set('save-accounts', accountsSaving || !accountsChanged);
  set('cancel-accounts', accountsSaving || !accountsChanged);
  set('discover-accounts', accountsSaving);
  set('save-sensors', !config || !sensorsDirty());
  updateDirty();
}

// Unsaved changes are computed against the last saved configuration, so editing a value back clears the mark.
const cityDirty = () => { const city = cities[Number($('city-results').value)]; return Boolean(city && city.id !== config.city?.id); };
const cpampDirty = () => trimUrl($('cpamp-url').value) !== trimUrl(config.cpampUrl) || $('management-key').value !== '';
const refreshDirty = () => $('quota-refresh-enabled').checked !== config.quotaRefreshEnabled || $('quota-refresh-minutes').value.trim() !== String(config.quotaRefreshMinutes);
const sensorsDirty = () => sensorsLoaded && Object.entries(sensorFields).some(([id, key]) => ($(id).value || null) !== (config.sensors?.[key] || null));
function accountsDirty() {
  if (!config) return false;
  const saved = config.accounts;
  return draftWatched.length !== saved.length || draftWatched.some((id, i) => id !== saved[i].id || effectiveAlias(id) !== saved[i].alias);
}
const sections = [['clock', '时间', clockDirty], ['city', '天气城市', cityDirty], ['weather', '天气来源', weatherDirty], ['ai-sources', 'AI 来源', aiSourcesDirty], ['cpamp', 'CPAMP 连接', cpampDirty], ['accounts', '账号', accountsDirty], ['refresh', '用量自动刷新', refreshDirty], ['sensors', '硬件传感器', sensorsDirty]];
const dirtySections = () => config ? sections.filter(([, , changed]) => changed()) : [];
function updateDirty() {
  const dirty = dirtySections(), keys = dirty.map(([key]) => key);
  for (const pill of document.querySelectorAll('[data-dirty-for]')) pill.hidden = !keys.includes(pill.dataset.dirtyFor);
  if (keys.join() !== unsavedKey) {
    unsavedKey = keys.join();
    const box = $('unsaved'); box.replaceChildren(); box.hidden = !dirty.length;
    if (dirty.length) {
      box.append('未保存的更改：');
      dirty.forEach(([key, name], index) => {
        if (index) box.append('、');
        const link = document.createElement('a'); link.href = '#' + key + '-section'; link.textContent = name; box.append(link);
      });
      const tail = document.createElement('span'); tail.className = 'unsaved-tail'; tail.textContent = '离开或刷新页面前请先保存。';
      box.append('。', tail);
    }
  }
  return dirty.length > 0;
}
addEventListener('beforeunload', event => { if (updateDirty()) { event.preventDefault(); event.returnValue = ''; } });
// Any edit can change both the unsaved marks and which save buttons are available.
document.addEventListener('input', syncControls);
document.addEventListener('change', syncControls);

async function readConfig(reset = []) {
  // Keep other sections' drafts when one section saves and the configuration is read again.
  const keep = new Set(dirtySections().map(([key]) => key).filter(key => !reset.includes(key)));
  const previousAi = config?.aiSources;
  config = await api('config/read');
  if (reset.includes('ai-sources') || previousAi?.source !== config.aiSources?.source) aiSourceVersion++;
  for (const source of ['magpie', ...remoteAiSources]) {
    const connectionChanged = previousAi && (previousAi[source + 'Url'] !== config.aiSources?.[source + 'Url'] ||
      source === 'cpa' && previousAi.cpaApiVersion !== config.aiSources?.cpaApiVersion ||
      reset.includes('ai-sources') && ($('ai-' + source + '-key').value !== '' || $('ai-' + source + '-clear-key').checked));
    if (connectionChanged) {
      aiSourceVersion++; aiDetected[source] = [];
      if (savedAiSources().source === source) $('ai-sources-accounts').replaceChildren();
    }
  }
  if (!keep.has('clock')) applyClockDraft();
  if (!keep.has('weather')) applyWeatherDraft();
  if (!keep.has('ai-sources')) applyAiSourcesDraft();
  else renderAiSourcesDraft();
  if (!keep.has('cpamp')) $('cpamp-url').value = config.cpampUrl;
  $('current-city').textContent = '当前：' + config.city.name;
  $('credential-status').textContent = config.managementConfigured ? '已保存加密凭据。留空可保留。' : '尚未填写管理凭据。';
  $('ticket').value = config.readToken;
  if (!keep.has('refresh')) {
    $('quota-refresh-enabled').checked = config.quotaRefreshEnabled;
    $('quota-refresh-minutes').value = String(config.quotaRefreshMinutes);
  }
  if (sensorsLoaded && !keep.has('sensors')) applySensorSelection();
  if (!keep.has('accounts')) resetAccountDraft();
  renderAccounts(); renderRefresh(config.quotaRefresh); renderDiagnostics();
  // Same-origin preview remembers only the limited display ticket.
  try { const s = JSON.parse(localStorage.getItem('wallpaper.dashboard.v1') || '{}'); s.readToken = config.readToken; s.bridgeUrl = config.bridgeUrl; localStorage.setItem('wallpaper.dashboard.v1', JSON.stringify(s)); } catch { }
  void readAccountPlans();
  if (config.aiSources && savedAiSources().source !== 'cpamp') void readAiSources();
}

function applyAiSourcesDraft() {
  const saved = savedAiSources();
  $('ai-source').value = saved.source;
  $('ai-magpie-url').value = saved.magpieUrl;
  $('ai-magpie-key').value = ''; $('ai-magpie-clear-key').checked = false;
  for (const source of remoteAiSources) { $('ai-' + source + '-url').value = saved[source + 'Url']; $('ai-' + source + '-key').value = ''; $('ai-' + source + '-clear-key').checked = false; }
  $('ai-cpa-version').value = saved.cpaApiVersion;
  for (const source of readerSources) {
    const options = savedAiOptions(source);
    $(source + '-auto-refresh').checked = options.autoRefreshEnabled;
    $(source + '-refresh-minutes').value = options.refreshMinutes;
    $(source + '-all-accounts').checked = options.accounts === null;
    aiSelections[source] = (options.accounts || []).map(a => ({ ...a }));
    renderAiAccountEditor(source);
  }
  renderAiSourcesDraft();
}
function renderAiSourcesDraft() {
  const saved = savedAiSources(), source = $('ai-source').value, changedUrl = trimUrl($('ai-magpie-url').value) !== trimUrl(saved.magpieUrl);
  $('ai-magpie-fields').hidden = source !== 'magpie';
  $('ai-source-help').textContent = {
    cpamp: '使用下方 CPAMP 连接和关注列表，按保存的顺序显示账号。',
    local: '自动检测本机支持的 AI agent，读取当前 Windows 用户已登录账号的套餐与用量；可在下方配置账号显示、别名、顺序和刷新频率。',
    magpie: '读取 Magpie 已保留（kept）的账号缓存，可配置显示账号与刷新频率；更新上游缓存请使用 Magpie。',
    cpa: '直接通过 CPA 的管理接口读取托管账号，不经过 CPAMP。连接与账号设置见下方。',
    sub2api: '读取 Sub2API 后台托管的上游账号套餐与余量，不读取站内用户订阅或消费余额。'
  }[source] || '';
  $('ai-sources-current-source').textContent = `当前生效：${aiSourceNames[saved.source] || saved.source}` + (source !== saved.source ? `；保存后切换为${aiSourceNames[source]}。` : '。');
  for (const id of ['cpamp-section', 'accounts-section', 'refresh-section']) {
    $(id).hidden = source !== 'cpamp';
    document.querySelector(`.nav a[href="#${id}"]`).hidden = source !== 'cpamp';
  }
  for (const kind of readerSources) {
    $(kind + '-settings-section').hidden = source !== kind;
    document.querySelector(`.nav a[href="#${kind}-settings-section"]`).hidden = source !== kind;
    $(kind + '-accounts-hint').textContent = saved.source === kind ? '账号来自最近一次读取；隐藏只影响壁纸显示，不会停止对该账号的查询。' : '先保存来源切换，再读取此来源账号。';
  }
  $('ai-sources-refresh-help').hidden = source === 'cpamp';
  $('ai-sources-accounts').hidden = saved.source === 'cpamp';
  if ($('ai-sources-accounts').dataset.source !== saved.source) {
    $('ai-sources-accounts').dataset.source = saved.source;
    $('ai-sources-accounts').replaceChildren();
    $('ai-sources-status').textContent = `正在读取${aiSourceNames[saved.source] || saved.source}的账号…`;
  }
  if (saved.source === 'cpamp') $('ai-sources-status').textContent = '当前使用 CPAMP；连接、关注账号与用量刷新设置见下方。';
  $('ai-magpie-clear-key').disabled = !saved.magpieKeyConfigured;
  $('ai-magpie-key').disabled = $('ai-magpie-clear-key').checked;
  for (const kind of remoteAiSources) {
    const savedKey = saved[kind + 'KeyConfigured'], changed = trimUrl($('ai-' + kind + '-url').value) !== trimUrl(saved[kind + 'Url']);
    $('ai-' + kind + '-clear-key').disabled = !savedKey;
    $('ai-' + kind + '-key').disabled = $('ai-' + kind + '-clear-key').checked;
    $('ai-' + kind + '-key-status').textContent = savedKey && !changed ? '密钥已加密保存；留空保留，填写新值替换。' : changed && savedKey ? '地址已更改，请填写新密钥或勾选清除。' : '尚未配置管理密钥。';
  }
  $('ai-magpie-key-status').textContent = saved.magpieKeyConfigured && !changedUrl ? '当前地址的密钥已加密保存；留空保留，填写新值替换。' : changedUrl && saved.magpieKeyConfigured ? '地址已更改；请填写新地址的密钥，或勾选清除后保存。' : '如 Magpie 已启用 API 鉴权，请填写只读密钥。';
}
function renderAiAccountEditor(source) {
  const all = $(source + '-all-accounts').checked;
  const selected = aiSelections[source], detected = aiDetected[source];
  const byId = new Map(detected.map(a => [a.id, a]));
  const ids = [...selected.map(a => a.id), ...detected.filter(a => !selected.some(s => s.id === a.id)).map(a => a.id)];
  $(source + '-account-editor').replaceChildren(...ids.map((id, index) => {
    const account = byId.get(id), selection = selected.find(a => a.id === id);
    const row = document.createElement('li'); row.className = 'ai-account-editor-row'; row.dataset.accountId = id;
    const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.checked = all || Boolean(selection); checkbox.disabled = all;
    checkbox.setAttribute('aria-label', '显示 ' + (account?.alias || selection?.alias || '暂未发现的账号'));
    checkbox.addEventListener('change', () => {
      aiSelections[source] = checkbox.checked ? [...aiSelections[source], { id, alias: '' }] : aiSelections[source].filter(a => a.id !== id);
      renderAiAccountEditor(source); syncControls();
    });
    const field = document.createElement('div'); field.className = 'field';
    const label = document.createElement('label'); label.textContent = account?.alias || selection?.alias || '暂未发现的账号';
    const input = document.createElement('input'); input.type = 'text'; input.maxLength = 60; input.value = selection?.alias || ''; input.placeholder = '显示别名（留空使用原名称）'; input.disabled = all || !selection;
    input.id = `ai-${source}-alias-${index}`; label.htmlFor = input.id;
    input.addEventListener('input', () => { const item = aiSelections[source].find(a => a.id === id); if (item) item.alias = input.value; });
    field.append(label, input);
    const controls = document.createElement('div'); controls.className = 'actions';
    for (const [delta, title] of [[-1, '上移'], [1, '下移']]) {
      const button = document.createElement('button'); button.className = 'quiet'; button.textContent = title; button.setAttribute('aria-label', title + ' ' + label.textContent);
      const position = selected.findIndex(a => a.id === id); button.disabled = all || position < 0 || position + delta < 0 || position + delta >= selected.length;
      button.addEventListener('click', () => { const items = aiSelections[source]; [items[position], items[position + delta]] = [items[position + delta], items[position]]; renderAiAccountEditor(source); syncControls(); });
      controls.append(button);
    }
    const meta = document.createElement('div'); meta.className = 'account-meta'; meta.textContent = account ? `${providerName(account.provider)} · ${account.plan || '套餐待获取'} · ${account.windows?.length || 0} 个额度窗口` : '最近一次读取未发现，保留已保存的显示设置';
    row.append(checkbox, field, controls, meta); return row;
  }));
}
for (const source of readerSources) {
  $(source + '-all-accounts').addEventListener('change', () => {
    if (!$(source + '-all-accounts').checked && savedAiOptions(source).accounts === null && aiSelections[source].length === 0)
      aiSelections[source] = aiDetected[source].map(a => ({ id: a.id, alias: '' }));
    renderAiAccountEditor(source); syncControls();
  });
  action('save-' + source + '-settings', saveAiSources);
  action('cancel-' + source + '-settings', async () => { applyAiSourcesDraft(); status('已恢复保存的 AI 配置'); });
  action('refresh-' + source + '-settings', async () => { const result = await api('ai-sources/refresh'); status(result.message); await readAiSources(); });
}
function validMagpieUrl(value) {
  try { const url = new URL(value); return (url.protocol === 'https:' || url.protocol === 'http:' && ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname)) && !url.username && !url.password && !url.search && !url.hash && (url.pathname === '/' || url.pathname === ''); } catch { return false; }
}
for (const source of remoteAiSources) {
  $('ai-' + source + '-url').addEventListener('input', () => { $('ai-' + source + '-key').value = ''; renderAiSourcesDraft(); });
  $('ai-' + source + '-clear-key').addEventListener('change', () => { if ($('ai-' + source + '-clear-key').checked) $('ai-' + source + '-key').value = ''; renderAiSourcesDraft(); });
}
$('ai-source').addEventListener('change', renderAiSourcesDraft);
$('ai-magpie-url').addEventListener('input', () => { $('ai-magpie-key').value = ''; renderAiSourcesDraft(); });
$('ai-magpie-clear-key').addEventListener('change', () => { if ($('ai-magpie-clear-key').checked) $('ai-magpie-key').value = ''; renderAiSourcesDraft(); });
async function saveAiSources() {
  const draft = aiSourcesDraft(), magpieApiKey = $('ai-magpie-key').value.trim(), clearMagpieApiKey = $('ai-magpie-clear-key').checked;
  if (!Object.hasOwn(aiSourceNames, draft.source)) throw new Error('请选择有效的 AI 套餐来源');
  for (const source of readerSources) {
    const options = draft[source + 'Options'];
    if (!Number.isInteger(options.refreshMinutes) || options.refreshMinutes < 1 || options.refreshMinutes > 10080) throw new Error(aiSourceNames[source] + '刷新间隔需为 1–10080 的整数分钟');
  }
  if (!validMagpieUrl(draft.magpieUrl)) throw new Error('Magpie 地址须为 HTTPS 或本机回环 HTTP 地址，不含路径、凭据、查询参数或片段');
  if (magpieApiKey.length > 4096 || /[\u0000-\u001f\u007f]/.test(magpieApiKey)) throw new Error('Magpie API Key 格式无效');
  if (draft.magpieUrl !== trimUrl(savedAiSources().magpieUrl) && savedAiSources().magpieKeyConfigured && !magpieApiKey && !clearMagpieApiKey) throw new Error('Magpie 地址已更改，请填写新密钥或勾选清除已保存的密钥');
  const remoteKeys = {};
  for (const source of remoteAiSources) {
    const url = draft[source + 'Url'], key = $('ai-' + source + '-key').value.trim(), clear = $('ai-' + source + '-clear-key').checked;
    if (url && !validMagpieUrl(url)) throw new Error(aiSourceNames[source] + ' 地址须为 HTTPS 或本机回环 HTTP 根地址');
    if (key.length > 4096 || /[\u0000-\u001f\u007f]/.test(key)) throw new Error('管理密钥格式无效');
    if (url !== trimUrl(savedAiSources()[source + 'Url']) && savedAiSources()[source + 'KeyConfigured'] && !key && !clear) throw new Error(aiSourceNames[source] + ' 地址已更改，请填写新密钥或勾选清除');
    if (key) remoteKeys[source + 'Key'] = key;
    if (clear) remoteKeys['clear' + source[0].toUpperCase() + source.slice(1) + 'Key'] = true;
  }
  const result = await api('config', { aiSources: { ...draft, ...remoteKeys, ...(magpieApiKey ? { magpieApiKey } : {}), ...(clearMagpieApiKey ? { clearMagpieApiKey: true } : {}) } });
  for (const source of remoteAiSources) if (remoteKeys[source + 'Key'] || remoteKeys['clear' + source[0].toUpperCase() + source.slice(1) + 'Key']) aiDetected[source] = [];
  await readConfig(['ai-sources']); status(result.message || 'AI 来源已保存，正在检测账号');
}
action('save-ai-sources', saveAiSources);
action('cancel-ai-sources', async () => { applyAiSourcesDraft(); status('已恢复保存的 AI 来源'); });
async function readAiSources() {
  if (aiSourcesLoading || !config?.aiSources || savedAiSources().source === 'cpamp') return;
  const source = savedAiSources().source, origin = savedAiSources()[savedAiSources().source + 'Url'], version = aiSourceVersion;
  const isCurrent = () => aiSourceVersion === version && savedAiSources().source === source && (source === 'local' || savedAiSources()[source + 'Url'] === origin);
  aiSourcesLoading = true; aiSourcesReadAt = Date.now(); syncControls();
  try {
    const data = await api('ai-sources/status');
    if (!isCurrent()) return;
    aiDetected[source] = Array.isArray(data.accounts) ? data.accounts : [];
    // Never replace an editor while a user is typing or has an unsaved selection.
    if (!aiSourcesDirty() && !$(source + '-account-editor').contains(document.activeElement)) renderAiAccountEditor(source);
    $('ai-sources-status').textContent = data.message || '暂无可展示的本机账号';
    $('ai-sources-accounts').replaceChildren(...(Array.isArray(data.accounts) ? data.accounts : []).map(account => {
      const row = document.createElement('li'); row.className = 'result-row'; row.dataset.accountId = account.id;
      const title = document.createElement('strong'); title.textContent = account.alias || providerName(account.provider);
      const meta = document.createElement('div'); meta.className = 'account-meta';
      for (const label of [providerName(account.provider), account.plan || '套餐待获取', `${Array.isArray(account.windows) ? account.windows.length : 0} 个额度窗口`]) {
        const badge = document.createElement('span'); badge.textContent = label; meta.append(badge);
      }
      row.append(title, meta); return row;
    }));
    const report = await api('ai-sources/refresh-status');
    if (isCurrent()) {
      const stamp = value => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '暂无';
      $(source + '-refresh-state').textContent = `${report.state.message} · 最近成功：${stamp(report.state.lastSuccessAt)} · 下次：${report.nextRefreshAt ? stamp(report.nextRefreshAt) : report.enabled ? '等待读取' : '自动刷新已关闭'}`;
    }
  } catch { if (isCurrent()) $('ai-sources-status').textContent = '暂时无法读取 AI 来源状态，请稍后重试'; }
  finally { aiSourcesLoading = false; syncControls(); if (!isCurrent()) void readAiSources(); }
}
action('refresh-ai-sources', async () => {
  const result = await api('ai-sources/refresh'); status(result.message || '正在刷新已保存的 AI 来源'); await readAiSources();
});

function applyClockDraft() {
  const clock = savedClock();
  $('clock-mode-status').textContent = config?.clock ? '已启用管理页时间设置，保存后自动同步到壁纸。' : '管理页时间设置尚未启用，壁纸继续使用已有设置。点击保存可启用这里的时区与校准配置。';
  $('clock-timezone').value = clock.timezone;
  $('clock-offset').value = String(clock.offsetSeconds);
  $('clock-seconds').checked = clock.seconds;
  $('clock-hour12').checked = clock.hour12;
  $('clock-world-zones').value = clock.worldZones.join('\n');
  renderClockPreview();
}
function renderClockPreview() {
  const clock = clockDraft();
  if (!validTimezone(clock.timezone) || clock.offsetSeconds === null || !Number.isFinite(clock.offsetSeconds) || Math.abs(clock.offsetSeconds) > 86400) {
    $('clock-preview-time').textContent = '请检查时区或校准值'; $('clock-preview-date').textContent = '输入有效值后显示预览'; return;
  }
  const now = new Date(Date.now() + clock.offsetSeconds * 1000);
  $('clock-preview-time').textContent = new Intl.DateTimeFormat('en-GB', { timeZone: clock.timezone, hour: '2-digit', minute: '2-digit', ...(clock.seconds ? { second: '2-digit' } : {}), hour12: clock.hour12 }).format(now);
  $('clock-preview-date').textContent = new Intl.DateTimeFormat('zh-CN', { timeZone: clock.timezone, year: 'numeric', month: '2-digit', day: '2-digit', weekday: 'long' }).format(now) + ' · ' + clock.timezone;
}
function applyWeatherDraft() {
  $('weather-provider').value = savedWeather().provider;
  $('weather-api-key').value = ''; $('weather-clear-key').checked = false;
  applyWeatherLocationFields();
  renderWeatherProvider();
}
function applyWeatherLocationFields() {
  const same = $('weather-provider').value === savedWeather().provider;
  $('weather-api-host').value = same && savedWeather().provider === 'qweather' ? savedWeather().apiHost || '' : '';
  $('weather-city-code').value = same && savedWeather().provider === 'amap' ? savedWeather().cityCode || '' : '';
}
function renderWeatherProvider() {
  const provider = $('weather-provider').value, keyed = ['openweathermap', 'weatherapi', 'amap', 'qweather'].includes(provider);
  const same = provider === savedWeather().provider, savedHasKey = same && savedWeather().apiKeyConfigured, hasKey = weatherCanRetainKey();
  $('weather-city-code-field').hidden = provider !== 'amap';
  $('weather-api-host-field').hidden = provider !== 'qweather';
  $('weather-api-key').placeholder = provider === 'amap' ? '填写高德 Web 服务 Key（不是 JS API Key）' : '填写所选天气来源的 API Key';
  $('weather-key-field').hidden = !keyed;
  $('weather-clear-key').disabled = !savedHasKey;
  $('weather-api-key').disabled = $('weather-clear-key').checked;
  $('weather-key-status').textContent = hasKey ? '已加密保存当前来源的密钥；留空保留，输入新值替换。' : provider === 'qweather' && savedHasKey ? 'API Host 已更改，请重新填写该主机的 API Key；原主机的密钥不会沿用。也可清除已保存密钥后保存为未配置。' : '请填写当前来源的 API Key；其他来源的密钥不会沿用。';
  $('weather-provider-help').textContent = {
    'open-meteo': 'Open-Meteo 天气模型，无需 API Key。',
    'met-no': 'MET Norway / Yr 天气预报，无需 API Key。',
    'openweathermap': '使用 OpenWeatherMap 的天气与预报接口，需要有效 API Key。',
    'weatherapi': '使用 WeatherAPI 天气与预报接口，需要有效 API Key。',
    'amap': '高德天气支持国内城市的实况与每日预报，需要 Web 服务 Key（不是 JS API Key）；不提供小时预报。',
    'qweather': '和风天气使用控制台 API Key 与专属 API Host，按所选城市坐标读取天气。'
  }[provider] || '';
}
for (const id of ['clock-timezone', 'clock-offset', 'clock-seconds', 'clock-hour12']) $(id).addEventListener('input', renderClockPreview);
setInterval(() => { if (!document.hidden) renderClockPreview(); }, 1000);
$('weather-provider').addEventListener('change', () => {
  // A typed credential belongs only to the provider selected when it was entered.
  $('weather-api-key').value = ''; $('weather-clear-key').checked = false; applyWeatherLocationFields(); renderWeatherProvider(); syncControls();
});
$('weather-clear-key').addEventListener('change', () => { if ($('weather-clear-key').checked) $('weather-api-key').value = ''; renderWeatherProvider(); syncControls(); });
$('weather-api-host').addEventListener('input', renderWeatherProvider);
action('save-clock', async () => {
  const clock = clockDraft();
  if (!validTimezone(clock.timezone)) throw new Error('请输入有效时区，例如 Asia/Shanghai 或 Europe/London');
  if (clock.offsetSeconds === null || !Number.isFinite(clock.offsetSeconds) || Math.abs(clock.offsetSeconds) > 86400) throw new Error('时间校准须为 -86400 到 86400 之间的秒数');
  if (clock.worldZones.length > 24) throw new Error('世界时钟最多填写 24 个时区');
  const invalid = clock.worldZones.find(zone => !validTimezone(zone));
  if (invalid) throw new Error('世界时钟时区无效：' + invalid);
  await api('config', { clock }); await readConfig(['clock']); status('时间设置已保存，壁纸将自动更新');
});
action('cancel-clock', async () => { applyClockDraft(); status('已恢复保存的时间设置'); });
action('reset-clock-offset', async () => { $('clock-offset').value = '0'; renderClockPreview(); $('clock-sync-status').textContent = '已恢复使用系统时间；保存时间设置后生效。'; });
action('sync-clock', async () => {
  status('正在获取网络时间…');
  const result = await api('time/sync');
  if (!Number.isFinite(result.offsetSeconds) || Math.abs(result.offsetSeconds) > 86400) throw new Error('网络校时结果超出可用范围，请手动填写校准值');
  $('clock-offset').value = String(Math.round(result.offsetSeconds * 1000) / 1000); renderClockPreview();
  $('clock-sync-status').textContent = `${result.source || 'HTTPS 时间'} · 校准 ${$('clock-offset').value} 秒` + (Number.isFinite(result.roundTripMs) ? ` · 往返 ${Math.round(result.roundTripMs)} 毫秒` : '') + '。已填入预览，保存时间设置后生效。';
  status('网络校时结果已填入，请预览后保存时间设置');
});
action('save-weather', async () => {
  const provider = $('weather-provider').value, apiKey = $('weather-api-key').value.trim(), clearApiKey = $('weather-clear-key').checked;
  const keyed = ['openweathermap', 'weatherapi', 'amap', 'qweather'].includes(provider);
  if (apiKey.length > 512 || /[\u0000-\u001f\u007f]/.test(apiKey)) throw new Error('天气 API Key 格式无效');
  if (keyed && !clearApiKey && !apiKey && !weatherCanRetainKey()) throw new Error(provider === 'qweather' && provider === savedWeather().provider && savedWeather().apiKeyConfigured ? 'API Host 已更改，请重新填写对应的 API Key，或清除已保存密钥后保存' : '请填写当前天气来源的 API Key');
  if (provider === 'amap' && !/^[0-9]{6}$/.test(weatherCityCode())) throw new Error('请填写高德的 6 位城市行政区代码，例如苏州 320500');
  if (provider === 'qweather' && !validWeatherApiHost(weatherApiHost())) throw new Error('请填写有效和风 API Host，例如 demo.re.qweatherapi.com，不含协议、端口或路径');
  await api('config', { weather: { provider, ...(apiKey ? { apiKey } : {}), ...(clearApiKey ? { clearApiKey: true } : {}), ...(provider === 'amap' ? { cityCode: weatherCityCode() } : {}), ...(provider === 'qweather' ? { apiHost: weatherApiHost() } : {}) } });
  $('weather-api-key').value = ''; await readConfig(['weather']); status('天气来源已保存，正在更新天气');
});
action('cancel-weather', async () => { applyWeatherDraft(); status('已恢复保存的天气来源'); });

function resetAccountDraft() {
  draftWatched = config.accounts.map(a => a.id);
  draftAlias = new Map(config.accounts.map(a => [a.id, a.alias]));
}
function accountInfo(id) {
  const saved = config.accounts.find(a => a.id === id), found = discovered?.find(a => a.id === id);
  return { id, provider: saved?.provider || found?.provider || '', base: saved?.alias || found?.alias || '', label: found?.label || '', missing: Boolean(discovered && !found) };
}
const aliasValue = id => draftAlias.has(id) ? draftAlias.get(id) : accountInfo(id).base;
const effectiveAlias = id => aliasValue(id).trim() || accountInfo(id).base;
function accountIds() {
  // Watched accounts first in display order, then the rest in CPAMP order.
  const rest = [];
  for (const id of [...(discovered || []).map(a => a.id), ...config.accounts.map(a => a.id)]) if (!draftWatched.includes(id) && !rest.includes(id)) rest.push(id);
  return [...draftWatched, ...rest];
}
const filtersActive = () => Boolean($('account-search').value.trim() || $('account-provider').value || $('account-watch').value);
function accountMatches(id) {
  const info = accountInfo(id), watched = draftWatched.includes(id), view = $('account-watch').value, provider = $('account-provider').value;
  if (provider && info.provider !== provider) return false;
  if ((view === 'watched' && !watched) || (view === 'unwatched' && watched)) return false;
  const query = $('account-search').value.trim().toLowerCase();
  return !query || [aliasValue(id), info.base, info.provider, providerName(info.provider), info.label, accountPlans.get(id) || ''].some(text => text.toLowerCase().includes(query));
}
function syncProviderOptions(ids) {
  const select = $('account-provider'), providers = [...new Set(ids.map(id => accountInfo(id).provider).filter(Boolean))].sort();
  if ([...select.options].slice(1).map(o => o.value).join() === providers.join()) return;
  const current = select.value;
  while (select.options.length > 1) select.remove(1);
  for (const provider of providers) select.add(new Option(providerName(provider), provider));
  select.value = providers.includes(current) ? current : '';
}
function captureFocus() {
  const node = document.activeElement, row = node?.closest?.('.account-row');
  if (!row || !node.dataset.control) return null;
  return { id: row.dataset.accountId, control: node.dataset.control, index: [...row.parentElement.children].indexOf(row), start: node.selectionStart ?? null, end: node.selectionEnd ?? null };
}
function restoreFocus(target) {
  if (!target) return;
  const rows = [...$('account-list').children], row = rows.find(r => r.dataset.accountId === target.id);
  if (!row) {
    // The row left the current filter: keep the keyboard position in the list.
    const next = rows[Math.min(target.index, rows.length - 1)];
    (next?.querySelector(`[data-control="${target.control}"]:not(:disabled)`) || next?.querySelector('[data-control=watch]') || $('account-search')).focus();
    return;
  }
  const controls = [...row.querySelectorAll('[data-control]')], exact = controls.find(c => c.dataset.control === target.control && !c.disabled);
  const node = exact || (target.control.startsWith('move') && controls.find(c => c.dataset.control.startsWith('move') && !c.disabled)) || row.querySelector('[data-control=watch]');
  node.focus();
  if (node === exact && target.start !== null && node.setSelectionRange) node.setSelectionRange(target.start, target.end);
}
function rowLabels(row, id) {
  const name = effectiveAlias(id);
  row.querySelector('[data-control=watch]').setAttribute('aria-label', '在壁纸上显示 ' + name);
  for (const button of row.querySelectorAll('.order-controls button')) button.setAttribute('aria-label', (button.dataset.control === 'move-1' ? '上移 ' : '下移 ') + name);
}
function accountRow(id, visibleWatched) {
  const info = accountInfo(id), watched = draftWatched.includes(id), position = draftWatched.indexOf(id) + 1;
  const item = document.createElement('li'); item.className = 'account-row';
  Object.assign(item.dataset, { accountId: id, provider: info.provider, watched: String(watched) });
  // Pointer-only grip; keyboard and screen reader users reorder with the move buttons.
  const handle = document.createElement('span'); handle.className = 'drag-handle'; handle.append(svgIcon('grip'));
  handle.setAttribute('aria-hidden', 'true'); handle.title = '拖动调整显示顺序';
  handle.dataset.disabled = String(accountsSaving || !watched || visibleWatched.length < 2);
  handle.addEventListener('pointerdown', event => startDrag(event, id, item));
  handle.addEventListener('pointermove', event => { if (drag?.pointerId === event.pointerId) drag.y = event.clientY; });
  handle.addEventListener('pointerup', event => { if (drag?.pointerId === event.pointerId) { drag.y = event.clientY; finishDrag(true); } });
  for (const type of ['pointercancel', 'lostpointercapture']) handle.addEventListener(type, event => { if (drag?.pointerId === event.pointerId) finishDrag(false); });
  const check = document.createElement('input'); check.type = 'checkbox'; check.className = 'watch-toggle'; check.checked = watched; check.dataset.control = 'watch'; check.disabled = accountsSaving;
  check.addEventListener('change', () => toggleWatch(id, check.checked));
  const order = document.createElement('span'); order.className = 'order-position'; order.textContent = watched ? String(position) : '–';
  order.title = watched ? `壁纸显示第 ${position} 位` : '未在壁纸上显示';
  const main = document.createElement('div'); main.className = 'account-main';
  const alias = document.createElement('input'); alias.type = 'text'; alias.className = 'alias-input'; alias.value = aliasValue(id); alias.maxLength = 60;
  alias.placeholder = info.base; alias.dataset.control = 'alias'; alias.disabled = accountsSaving;
  alias.setAttribute('aria-label', '显示别名 ' + (info.label || info.base));
  alias.addEventListener('input', () => { draftAlias.set(id, alias.value); accountNotice = ''; rowLabels(item, id); renderAccountSummary(); syncControls(); });
  const meta = document.createElement('div'); meta.className = 'account-meta';
  const provider = document.createElement('span'); provider.className = 'provider-badge'; provider.textContent = providerName(info.provider);
  const plan = document.createElement('span'); plan.className = 'order-plan';
  meta.append(provider, plan);
  if (info.label) { const file = document.createElement('span'); file.className = 'account-file'; file.textContent = info.label; file.title = info.label; meta.append(file); }
  if (info.missing) { const missing = document.createElement('span'); missing.className = 'account-missing'; missing.textContent = '最近一次读取未在 CPAMP 中找到'; meta.append(missing); }
  main.append(alias, meta);
  const controls = document.createElement('div'); controls.className = 'order-controls';
  const at = visibleWatched.indexOf(id);
  for (const [delta, symbol, label] of [[-1, 'up', '上移'], [1, 'down', '下移']]) {
    const button = document.createElement('button'); button.type = 'button'; button.append(svgIcon(symbol)); button.title = label;
    button.dataset.control = 'move' + delta; button.disabled = accountsSaving || !watched || !visibleWatched[at + delta];
    button.addEventListener('click', () => moveAccount(id, delta));
    controls.append(button);
  }
  item.append(handle, check, order, main, controls); rowLabels(item, id);
  return item;
}
function renderAccounts() {
  if (!config) return;
  // Rebuilding the list detaches the dragged row, so an interrupted drag is cancelled first.
  if (drag) finishDrag(false);
  const focus = captureFocus(), ids = accountIds();
  syncProviderOptions(ids);
  const visible = ids.filter(accountMatches), visibleWatched = visible.filter(id => draftWatched.includes(id));
  $('account-list').replaceChildren(...visible.map(id => accountRow(id, visibleWatched)));
  const filter = $('account-filter-status');
  filter.hidden = !filtersActive() || !ids.length;
  if (!filter.hidden) {
    const hiddenWatched = draftWatched.length - visibleWatched.length;
    const clear = document.createElement('button'); clear.type = 'button'; clear.textContent = '清除筛选';
    clear.addEventListener('click', () => { $('account-search').value = ''; $('account-provider').value = ''; $('account-watch').value = ''; renderAccounts(); $('account-search').focus(); });
    filter.replaceChildren(`显示 ${visible.length} / ${ids.length} 个账号` + (!visible.length ? '，没有匹配的账号' : hiddenWatched && visibleWatched.length ? '；拖动和上移、下移只在可见的已关注账号之间调整' : ''), clear);
  }
  updateAccountPlans();
  renderAccountSummary();
  restoreFocus(focus);
  syncControls();
}
function renderAccountSummary() {
  const total = accountIds().length;
  $('account-count').textContent = total ? `已关注 ${draftWatched.length}` + (discovered ? ` / 共 ${total} 个账号` : ' 个账号') : '';
  const empty = !total ? (config.managementConfigured ? '还没有关注账号。点击「从 CPAMP 读取账号」，勾选要显示的账号后保存。' : '先保存 CPAMP 连接，再读取账号。') : !draftWatched.length ? '勾选要显示在壁纸上的账号后保存。' : '';
  $('account-status').textContent = accountsDirty() ? '有未保存的账号更改' : accountNotice || empty;
}
function toggleWatch(id, watched) {
  if (accountsSaving) return;
  draftWatched = draftWatched.filter(other => other !== id);
  if (watched) draftWatched.push(id);
  accountNotice = ''; renderAccounts();
}
function moveAccount(id, delta) {
  // With a filter active, move past the neighbouring visible account; hidden accounts keep their relative order.
  if (accountsSaving) return;
  const visible = draftWatched.filter(accountMatches), at = visible.indexOf(id), neighbor = visible[at + delta];
  if (at < 0 || !neighbor) return;
  const order = draftWatched.filter(other => other !== id), target = order.indexOf(neighbor);
  order.splice(delta < 0 ? target : target + 1, 0, id);
  draftWatched = order; accountNotice = ''; renderAccounts();
}
// Drag reordering: the grabbed row follows the pointer while the other visible watched rows open a slot.
// Like moveAccount, a filtered drop lands next to its visible neighbour and hidden accounts keep their relative order.
const dragEdge = 80;
function startDrag(event, id, row) {
  if (drag || accountsSaving || !event.isPrimary || event.button !== 0 || event.currentTarget.dataset.disabled === 'true') return;
  const rows = [...$('account-list').querySelectorAll('.account-row[data-watched=true]')], from = rows.indexOf(row);
  if (from < 0 || rows.length < 2) return;
  event.preventDefault();
  // Finish any drop animation before measuring, then use document coordinates so auto-scroll keeps the slots aligned.
  for (const r of rows) r.classList.remove('settling');
  const tops = rows.map(r => r.getBoundingClientRect().top + scrollY), heights = rows.map(r => r.getBoundingClientRect().height);
  drag = { id, row, rows, from, to: from, tops, heights, pointerId: event.pointerId, handle: event.currentTarget, startY: event.clientY + scrollY, y: event.clientY, frame: 0 };
  drag.handle.setPointerCapture(event.pointerId);
  row.classList.add('dragging'); $('account-list').classList.add('sorting'); document.documentElement.classList.add('row-dragging');
  drag.frame = requestAnimationFrame(dragFrame);
}
function dragLayout() {
  const { rows, from, tops, heights } = drag, end = rows.length - 1;
  // The slot follows the pointer itself; only the drawn row is kept inside the watched rows.
  const moved = drag.y + scrollY - drag.startY, center = tops[from] + moved + heights[from] / 2;
  const dy = Math.min(Math.max(moved, tops[0] - tops[from]), tops[end] + heights[end] - heights[from] - tops[from]);
  drag.to = rows.filter((r, j) => j !== from && tops[j] + heights[j] / 2 < center).length;
  rows.forEach((r, j) => {
    // 	o counts the other rows above the drop slot; rows between the old and new slot make room.
    const k = j < from ? j : j - 1, shift = j === from ? dy : j < from && k >= drag.to ? heights[from] : j > from && k < drag.to ? -heights[from] : 0;
    r.style.transform = shift ? `translateY(${shift}px)` : '';
  });
}
function dragFrame() {
  if (!drag) return;
  // Scroll while the pointer rests near the top or bottom of the window (the sticky dock sits at the bottom).
  const edge = Math.max(-dragEdge, Math.min(dragEdge, drag.y < dragEdge ? drag.y - dragEdge : drag.y > innerHeight - dragEdge ? drag.y - innerHeight + dragEdge : 0));
  if (edge) scrollBy(0, Math.round(edge / 5));
  dragLayout();
  drag.frame = requestAnimationFrame(dragFrame);
}
function settleRows(rows, fromTops) {
  // Animate rows from where they were drawn to their new places; reduced motion disables the transition.
  rows.forEach((r, i) => {
    const delta = fromTops[i] - r.getBoundingClientRect().top;
    r.classList.remove('settling'); r.style.transform = Math.abs(delta) >= 1 ? `translateY(${delta}px)` : '';
  });
  void $('account-list').offsetHeight;
  for (const r of rows) { r.classList.add('settling'); r.style.transform = ''; setTimeout(() => r.classList.remove('settling'), 260); }
}
function finishDrag(commit) {
  if (!drag) return;
  if (commit) dragLayout();
  const { id, row, rows, from, to, handle, pointerId } = drag;
  cancelAnimationFrame(drag.frame);
  drag = null;
  if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
  const drawn = rows.map(r => r.getBoundingClientRect().top);
  row.classList.remove('dragging'); $('account-list').classList.remove('sorting'); document.documentElement.classList.remove('row-dragging');
  for (const r of rows) r.style.transform = '';
  if (!commit || to === from || accountsSaving) { settleRows(rows, drawn); return; }
  const others = rows.map(r => r.dataset.accountId).filter(other => other !== id), order = draftWatched.filter(other => other !== id);
  order.splice(to > from ? order.indexOf(others[to - 1]) + 1 : order.indexOf(others[to]), 0, id);
  draftWatched = order; accountNotice = ''; renderAccounts();
  const moved = [...$('account-list').querySelectorAll('.account-row[data-watched=true]')];
  settleRows(moved, moved.map(r => { const i = rows.findIndex(old => old.dataset.accountId === r.dataset.accountId); return i < 0 ? r.getBoundingClientRect().top : drawn[i]; }));
}
document.addEventListener('keydown', event => { if (drag && event.key === 'Escape') { event.preventDefault(); finishDrag(false); } });
for (const id of ['account-search', 'account-provider', 'account-watch']) $(id).addEventListener(id === 'account-search' ? 'input' : 'change', renderAccounts);
action('save-accounts', async () => {
  if (!accountsDirty() || accountsSaving) return;
  const accounts = draftWatched.map(id => ({ id, alias: effectiveAlias(id) }));
  accountsSaving = true; renderAccounts();
  try {
    const r = await api('config', { accounts });
    accountsSaving = false;
    await readConfig(['accounts']);
    accountNotice = '账号设置已保存'; status(r.message);
  } finally { accountsSaving = false; renderAccounts(); }
});
$('cancel-accounts').addEventListener('click', () => {
  if (accountsSaving) return;
  resetAccountDraft(); accountNotice = '已恢复保存的账号设置'; renderAccounts();
});
action('discover-accounts', async () => {
  status('正在从 CPAMP 读取账号…');
  discovered = (await api('accounts')).accounts;
  renderAccounts();
  const fresh = discovered.filter(a => !draftWatched.includes(a.id)).length, missing = draftWatched.filter(id => !discovered.some(a => a.id === id)).length;
  status(!discovered.length ? '没有发现支持配额快照的账号' : `已读取 ${discovered.length} 个账号，其中 ${fresh} 个未关注` + (missing ? `；${missing} 个已关注账号未在 CPAMP 中找到` : '') + '。勾选后保存即可显示。');
});
async function readAccountPlans() {
  if (plansLoading || !config?.readToken || !config.accounts.length) return;
  const generation = plansGeneration;
  plansLoading = true; plansReadAt = Date.now();
  try {
    const response = await fetch('/v1/snapshot', { headers: { 'X-Wallpaper-Token': config.readToken }, cache: 'no-store', signal: AbortSignal.timeout(8000) });
    if (!response.ok) throw new Error('Snapshot unavailable');
    const snapshot = await response.json();
    if (!Array.isArray(snapshot.quota?.accounts)) throw new Error('Invalid snapshot');
    if (generation !== plansGeneration) return;
    accountPlans = new Map(snapshot.quota.accounts.map(a => [a.id, typeof a.plan === 'string' ? a.plan.trim() : '']));
    plansUnavailable = false;
  } catch { if (generation === plansGeneration) plansUnavailable = true; }
  finally { if (generation === plansGeneration) { plansLoading = false; updateAccountPlans(); } }
}
function clearAccountPlans() {
  plansGeneration++; accountPlans.clear(); plansUnavailable = false; plansLoading = false; plansReadAt = 0;
}
function updateAccountPlans() {
  // Update only metadata: background refreshes must preserve drafts and keyboard focus.
  for (const row of $('account-list').children) {
    const badge = row.querySelector('.order-plan'), id = row.dataset.accountId, plan = accountPlans.get(id);
    const providerFallback = row.dataset.provider === 'antigravity' && (!plan || plan === 'antigravity');
    // The wallpaper shows "antigravity" when no plan is known; here the provider chip already says so.
    row.querySelector('.provider-badge').title = providerFallback && row.dataset.watched === 'true' ? '暂未获取到套餐信息；获取成功后在旁边显示套餐名称。' : '';
    badge.hidden = row.dataset.watched !== 'true' || providerFallback;
    if (badge.hidden) continue;
    const pending = !plan && !config.accounts.some(a => a.id === id);
    badge.textContent = plan || (pending ? '保存后同步套餐' : plansUnavailable ? '套餐暂不可用' : '套餐待同步');
    badge.dataset.state = plan ? 'known' : plansUnavailable && !pending ? 'unavailable' : 'unknown';
    badge.title = plan ? `套餐：${plan}${plansUnavailable ? '（暂时无法更新，保留上次读取结果）' : '（最近的本机额度快照）'}` : pending ? '新关注的账号保存后，下一次额度快照会带上套餐名称。' : plansUnavailable ? '暂时无法读取本机快照，将自动重试；仍可调整并保存账号设置。' : '暂无套餐信息；可在下方立即刷新用量，或等待自动刷新。';
  }
}
function resultRow(account) {
  const row = document.createElement('div'); row.className = 'result-row'; row.dataset.status = account.status;
  row.textContent = account.alias + ' · ' + account.message;
  return row;
}
function renderRefresh(report) {
  const running = ['running', 'queued'].includes(report.state.status);
  refreshBusy = running;
  $('quota-refresh-summary').textContent = report.state.message;
  const time = value => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '暂无';
  $('quota-refresh-times').textContent = `上次尝试：${time(report.state.lastAttemptAt)} · 最近成功：${time(report.state.lastSuccessAt)} · 下次：${running ? '刷新完成后计算' : !report.enabled ? '自动刷新已关闭' : report.nextRefreshAt ? time(report.nextRefreshAt) : '请配置连接与关注账号'}`;
  // Accounts that were not updated stay visible; the full per-account list is folded away.
  const results = report.state.accounts || [], missed = results.filter(a => a.status !== 'ok');
  $('quota-refresh-failures').replaceChildren(...missed.map(resultRow));
  $('quota-refresh-details').hidden = !results.length;
  $('quota-refresh-details-summary').textContent = `各账号结果：${results.length - missed.length} 个已更新` + (missed.length ? ` · ${missed.length} 个未更新` : '');
  $('quota-refresh-accounts').replaceChildren(...results.map(resultRow));
  syncControls();
}
action('save-quota-refresh', async () => {
  const minutes = $('quota-refresh-minutes');
  if (!minutes.reportValidity()) return;
  const interval = Number(minutes.value);
  if (!Number.isInteger(interval) || interval < 1 || interval > 10080) throw new Error('请输入 1–10080 之间的整数分钟');
  await api('config', { quotaRefreshEnabled: $('quota-refresh-enabled').checked, quotaRefreshMinutes: interval });
  await readConfig(['refresh']); status('刷新设置已保存');
});
action('refresh-quota-now', async () => { const r = await api('quota-refresh'); renderRefresh(r.refresh); status(r.message); });
action('search-city', async () => {
  status('正在搜索城市…'); cities = (await api('cities', { query: $('city-query').value })).cities;
  $('city-results').replaceChildren(...cities.map((city, i) => new Option(city.name + ' (' + city.timezone + ')' + (city.id === config.city?.id ? ' · 当前' : ''), String(i))));
  if (!cities.length) $('city-results').append(new Option('未找到城市', ''));
  status(cities.length ? '请选择城市后保存' : '未找到城市，可尝试拼音或英文名');
});
action('save-city', async () => { const city = cities[Number($('city-results').value)]; if (!$('city-results').value || !city) throw new Error('请先搜索并选择城市'); const r = await api('config', { city }); await readConfig(); status(r.message); });
action('save-cpamp', async () => {
  const key = $('management-key').value, url = $('cpamp-url').value;
  // The bridge drops the watched accounts whenever the address or the credential changes.
  const sourceChanged = trimUrl(url) !== trimUrl(config.cpampUrl) || key !== '';
  const watched = config.accounts.length;
  if (sourceChanged && (watched || accountsDirty()) && !confirm(`更换面板地址或凭据会取消全部 ${watched} 个关注账号${accountsDirty() ? '，并放弃未保存的账号更改' : ''}；之后需要重新读取并勾选账号。继续保存？`)) { status('已取消保存连接'); return; }
  const r = await api('config', { cpampUrl: url, ...(key ? { managementKey: key } : {}) });
  $('management-key').value = '';
  if (sourceChanged) { discovered = null; clearAccountPlans(); }
  await readConfig(sourceChanged ? ['cpamp', 'accounts'] : ['cpamp']); status(r.message);
});
action('clear-cpamp', async () => {
  const watched = config.accounts.length;
  if (!confirm(watched ? `清除凭据会同时取消全部 ${watched} 个关注账号，壁纸将不再显示额度。确定清除？` : '确定清除已保存的 CPAMP 管理凭据？')) { status('已取消清除凭据'); return; }
  const r = await api('config', { clearManagementKey: true, accounts: [] });
  $('management-key').value = ''; discovered = null; clearAccountPlans(); await readConfig(['cpamp', 'accounts']); status(r.message);
});
function applySensorSelection() {
  for (const [id, key] of Object.entries(sensorFields)) {
    const value = config.sensors?.[key] || '', select = $(id);
    if (value && ![...select.options].some(o => o.value === value)) select.add(new Option('已保存的传感器（当前未检测到）', value));
    select.value = value;
  }
}
action('load-sensors', async () => {
  const sensors = (await api('sensors')).sensors;
  const draft = sensorsDirty() ? Object.fromEntries(Object.keys(sensorFields).map(id => [id, $(id).value])) : null;
  for (const id of Object.keys(sensorFields)) while ($(id).options.length > 1) $(id).remove(1);
  const seen = new Set();
  for (const s of sensors) {
    if (/^Gpu/.test(s.hardwareType) && !seen.has(s.hardwareId)) { seen.add(s.hardwareId); $('gpu-id').add(new Option(s.hardware, s.hardwareId)); }
    if (s.type !== 'Temperature') continue;
    const label = `${s.hardware} · ${s.name} (${s.value === null ? '不可用' : s.value.toFixed(1) + '°C'})`;
    if (s.hardwareType === 'Cpu') $('cpu-temp').add(new Option(label, s.id));
    if (/^Gpu/.test(s.hardwareType)) $('gpu-temp').add(new Option(label, s.id));
  }
  sensorsLoaded = true; applySensorSelection();
  if (draft) for (const [id, value] of Object.entries(draft)) if ([...$(id).options].some(o => o.value === value)) $(id).value = value;
  const lines = sensors.filter(s => ['Load', 'Temperature', 'Data', 'SmallData'].includes(s.type)).map(s => `${s.hardware} / ${s.name}: ${s.value === null ? '不可用' : s.value.toFixed(2)} (${s.type})`);
  $('sensor-report').textContent = lines.join('\n');
  $('sensor-summary').textContent = `原始传感器读数（${lines.length} 项）`;
  $('sensor-details').hidden = !lines.length;
  $('sensor-hint').textContent = `已检测到 ${seen.size} 个物理 GPU；选择后保存即可应用。`;
  status('传感器已检测');
});
action('save-sensors', async () => { const r = await api('config', { sensors: { gpuId: $('gpu-id').value || null, cpuTemperatureId: $('cpu-temp').value || null, gpuTemperatureId: $('gpu-temp').value || null } }); await readConfig(['sensors']); status(r.message); });
function ago(value) {
  const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000));
  return seconds < 10 ? '刚刚' : seconds < 60 ? `${seconds} 秒前` : seconds < 3600 ? `${Math.floor(seconds / 60)} 分钟前` : seconds < 86400 ? `${Math.floor(seconds / 3600)} 小时前` : `${Math.floor(seconds / 86400)} 天前`;
}
function moment(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '时间未知' : `${date.toLocaleString('zh-CN', { hour12: false })}（${ago(value)}）`;
}
function renderDiagnostics() {
  // Readable facts first; the raw report stays in the folded advanced section.
  const d = config?.diagnostics || {}, client = d.lastClient, faults = Array.isArray(d.cacheFaults) ? d.cacheFaults.length : 0;
  const quiet = client && Date.now() - new Date(client.at).getTime() > 5 * 60 * 1000;
  const facts = [
    ['壁纸前端', client ? [client.host === 'wallpaper-engine' ? 'Wallpaper Engine' : '浏览器预览', client.paused ? '已暂停' : '运行中', client.width && client.height ? `${client.width}×${client.height}` : '', client.fps ? `${client.fps} FPS` : ''].filter(Boolean).join(' · ') : '尚未收到壁纸上报'],
    ['最近上报', client ? moment(client.at) + (client.buildId ? ` · 构建 ${client.buildId}` : '') + (quiet ? ' · 较久未上报，壁纸可能已关闭或暂停' : '') : '—', quiet],
    ['最近读取快照', d.lastSnapshotRead ? moment(d.lastSnapshotRead) : '尚无'],
    ['缓存故障', faults ? `${faults} 项，详见高级诊断` : '无', faults > 0]
  ];
  $('connection-facts').replaceChildren(...facts.flatMap(([term, text, warn]) => {
    const dt = document.createElement('dt'), dd = document.createElement('dd');
    dt.textContent = term; dd.textContent = text; if (warn) dd.dataset.state = 'warn';
    return [dt, dd];
  }));
  $('diagnostics').textContent = JSON.stringify(d, null, 2);
}
async function readDiagnostics() {
  diagnosticsReadAt = Date.now();
  try { const latest = await api('config/read'); config.diagnostics = latest.diagnostics; renderDiagnostics(); } catch { }
}
action('copy-ticket', async () => {
  await navigator.clipboard.writeText(config.readToken); status('展示票据已复制');
  $('copy-ticket').textContent = '已复制';
  setTimeout(() => { $('copy-ticket').textContent = '复制'; }, 1600);
});
action('toggle-ticket', async () => {
  const shown = $('ticket').type === 'password';
  $('ticket').type = shown ? 'text' : 'password';
  $('toggle-ticket').textContent = shown ? '隐藏' : '显示'; $('toggle-ticket').setAttribute('aria-pressed', String(shown));
});
$('city-query').addEventListener('keydown', event => { if (event.key === 'Enter' && !event.isComposing) { event.preventDefault(); $('search-city').click(); } });
$('account-search').addEventListener('keydown', event => { if (event.key === 'Escape' && $('account-search').value) { event.preventDefault(); $('account-search').value = ''; renderAccounts(); } });
// Highlight the section in view; the navigation also carries the unsaved dots.
const navLinks = [...document.querySelectorAll('.nav a')];
const spy = new IntersectionObserver(entries => {
  for (const entry of entries) entry.target.dataset.visible = String(entry.isIntersecting);
  const current = [...document.querySelectorAll('main section')].find(section => section.dataset.visible === 'true');
  for (const link of navLinks) { if (current && link.hash === '#' + current.id) link.setAttribute('aria-current', 'true'); else link.removeAttribute('aria-current'); }
}, { rootMargin: '-20% 0px -55% 0px' });
for (const section of document.querySelectorAll('main section')) spy.observe(section);
// Liquid glint: one rAF-throttled CSS variable update for the panel under the pointer.
if (!matchMedia('(prefers-reduced-motion: reduce)').matches) {
  let frame = 0, panel = null, x = 0, y = 0;
  document.addEventListener('pointermove', event => {
    panel = event.target.closest?.('.glass'); x = event.clientX; y = event.clientY;
    if (!panel || frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0; const box = panel.getBoundingClientRect();
      panel.style.setProperty('--glint-x', `${x - box.left}px`); panel.style.setProperty('--glint-y', `${y - box.top}px`);
    });
  }, { passive: true });
}
action('refresh', async () => { const r = await api('refresh'); await readConfig(); status(r.message); });
(async () => {
  try {
    if (!['127.0.0.1', 'localhost', '[::1]'].includes(location.hostname)) throw new Error('本机设置只能从桥接服务打开');
    session = (await api('session')).session; await readConfig(); status('已连接。设置会保存在本机。');
    bridgeState('ok', '本机桥接已连接');
    diagnosticsReadAt = Date.now();
    let polling = false;
    setInterval(async () => {
      if (document.hidden || polling) return;
      polling = true;
      try { renderRefresh(await api('quota-refresh/status')); bridgeState('ok', '本机桥接已连接'); } catch { $('quota-refresh-summary').textContent = '暂时无法读取刷新状态，请检查本机桥接'; bridgeState('error', '本机桥接暂时无响应'); }
      finally { polling = false; }
      if (Date.now() - plansReadAt >= 15000) void readAccountPlans();
      if (Date.now() - diagnosticsReadAt >= 15000) void readDiagnostics();
      if (config.aiSources && savedAiSources().source !== 'cpamp' && Date.now() - aiSourcesReadAt >= 5000) void readAiSources();
    }, 3000);
  } catch (e) { status(e.message, 'error'); bridgeState('error', '无法连接本机桥接'); }
})();
