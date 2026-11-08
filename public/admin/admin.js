'use strict';
const $ = id => document.getElementById(id);
let session = '', config = null, cities = [], refreshBusy = false, sensorsLoaded = false;
// Account editor: config.accounts is the saved state; the draft keeps watch order and aliases until saved.
let discovered = null, draftWatched = [], draftAlias = new Map(), accountsSaving = false, accountNotice = '';
let accountPlans = new Map(), plansUnavailable = false, plansReadAt = 0, plansLoading = false, plansGeneration = 0;
let diagnosticsReadAt = 0, unsavedKey = '';
const busy = new Set();
const sensorFields = { 'gpu-id': 'gpuId', 'cpu-temp': 'cpuTemperatureId', 'gpu-temp': 'gpuTemperatureId' };
const providerNames = { codex: 'Codex', antigravity: 'Antigravity', claude: 'Claude', kimi: 'Kimi', xai: 'xAI', devin: 'Devin', meta: 'Meta' };
const providerName = provider => providerNames[provider] || provider || '未知平台';
const trimUrl = value => String(value || '').trim().replace(/\/+$/, '');
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
  set('refresh-quota-now', refreshBusy || !config?.managementConfigured || !config?.accounts.length);
  set('save-city', !config || !cityDirty());
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
const sections = [['city', '天气城市', cityDirty], ['cpamp', 'CPAMP 连接', cpampDirty], ['accounts', '账号', accountsDirty], ['refresh', '用量自动刷新', refreshDirty], ['sensors', '硬件传感器', sensorsDirty]];
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
  config = await api('config/read');
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
}

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
  item.append(check, order, main, controls); rowLabels(item, id);
  return item;
}
function renderAccounts() {
  if (!config) return;
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
    filter.replaceChildren(`显示 ${visible.length} / ${ids.length} 个账号` + (!visible.length ? '，没有匹配的账号' : hiddenWatched && visibleWatched.length ? '；上移和下移只在可见的已关注账号之间调整' : ''), clear);
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
    }, 3000);
  } catch (e) { status(e.message, 'error'); bridgeState('error', '无法连接本机桥接'); }
})();
