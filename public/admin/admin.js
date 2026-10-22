'use strict';
const $ = id => document.getElementById(id);
let session = '', config, cities = [], discovered = [];
let refreshDirty = false, refreshBusy = false;
let accountOrder = [], orderDirty = false, orderSaving = false;
const status = message => { $('status').textContent = message; };
async function api(path, body = {}, retry = true) {
  const response = await fetch('/v1/setup/' + path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Wallpaper-Setup': '1', 'X-Wallpaper-Admin': session }, body: JSON.stringify(body), cache: 'no-store' });
  const result = await response.json().catch(() => ({}));
  if (response.status === 403 && path !== 'session' && retry) { session = (await api('session', {}, false)).session; return api(path, body, false); }
  if (!response.ok) throw new Error(result.message || (response.status === 403 ? '设置会话已过期，请刷新此页' : '读取失败，请检查地址、鉴权和版本'));
  return result;
}
function action(id, fn) {
  $(id).addEventListener('click', async () => { $(id).disabled = true; try { await fn(); } catch (error) { status(error.message); } finally { $(id).disabled = id === 'refresh-quota-now' ? refreshBusy || !config?.managementConfigured || !config?.accounts.length : id === 'save-account-order' ? !orderDirty : false; } });
}
async function readConfig() {
  config = await api('config/read');
  $('cpamp-url').value = config.cpampUrl;
  $('current-city').textContent = '当前：' + config.city.name;
  $('credential-status').textContent = config.managementConfigured ? '已保存加密凭据。留空可保留。' : '尚未填写管理凭据。';
  $('ticket').value = config.readToken;
  $('diagnostics').textContent = JSON.stringify(config.diagnostics, null, 2);
  if (!refreshDirty) {
    $('quota-refresh-enabled').checked = config.quotaRefreshEnabled;
    $('quota-refresh-minutes').value = String(config.quotaRefreshMinutes);
  }
  renderRefresh(config.quotaRefresh);
  if (!orderDirty) { accountOrder = config.accounts.map(a => ({ ...a })); renderAccountOrder(); }
  // Same-origin preview remembers only the limited display ticket.
  try { const s = JSON.parse(localStorage.getItem('wallpaper.dashboard.v1') || '{}'); s.readToken = config.readToken; s.bridgeUrl = config.bridgeUrl; localStorage.setItem('wallpaper.dashboard.v1', JSON.stringify(s)); } catch { }
}
function renderAccountOrder() {
  $('account-order').replaceChildren();
  accountOrder.forEach((account, index) => {
    const item = document.createElement('li'); item.className = 'order-item'; item.dataset.orderId = account.id;
    const position = document.createElement('span'); position.className = 'order-position'; position.textContent = String(index + 1);
    const name = document.createElement('span'); name.className = 'order-name'; name.textContent = account.alias; name.title = account.alias;
    const controls = document.createElement('div'); controls.className = 'order-controls';
    for (const [delta, label, symbol] of [[-1, '上移', '↑'], [1, '下移', '↓']]) {
      const button = document.createElement('button'); button.type = 'button'; button.textContent = symbol; button.dataset.direction = String(delta);
      button.setAttribute('aria-label', label + ' ' + account.alias); button.title = label;
      button.disabled = orderSaving || index + delta < 0 || index + delta >= accountOrder.length;
      button.onclick = () => {
        if (orderSaving) return;
        const destination = index + delta;
        [accountOrder[index], accountOrder[destination]] = [accountOrder[destination], accountOrder[index]];
        orderDirty = true; renderAccountOrder();
        const moved = [...$('account-order').children].find(row => row.dataset.orderId === account.id);
        const buttons = [...moved.querySelectorAll('button')];
        (buttons.find(b => b.dataset.direction === String(delta) && !b.disabled) || buttons.find(b => !b.disabled))?.focus();
      };
      controls.append(button);
    }
    item.append(position, name, controls); $('account-order').append(item);
  });
  $('save-account-order').disabled = orderSaving || !orderDirty;
  $('account-order-status').textContent = !accountOrder.length ? '先选择关注账号' : orderDirty ? '顺序未保存' : '';
}
action('save-account-order', async () => {
  if (!orderDirty || orderSaving) return;
  orderSaving = true; renderAccountOrder();
  try {
    await api('config', { accounts: accountOrder.map(a => ({ id: a.id, alias: a.alias })) });
    orderDirty = false; await readConfig(); $('account-order-status').textContent = '显示顺序已保存'; status('显示顺序已保存');
  } finally { orderSaving = false; renderAccountOrder(); if (!orderDirty) $('account-order-status').textContent = '显示顺序已保存'; }
});
function renderRefresh(report) {
  const running = ['running', 'queued'].includes(report.state.status);
  refreshBusy = running;
  $('refresh-quota-now').disabled = running || !config?.managementConfigured || !config?.accounts.length;
  $('quota-refresh-summary').textContent = report.state.message;
  const time = value => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '暂无';
  $('quota-refresh-times').textContent = `上次尝试：${time(report.state.lastAttemptAt)} · 最近成功：${time(report.state.lastSuccessAt)} · 下次：${running ? '刷新完成后计算' : !report.enabled ? '自动刷新已关闭' : report.nextRefreshAt ? time(report.nextRefreshAt) : '请配置连接与关注账号'}`;
  $('quota-refresh-accounts').replaceChildren();
  for (const account of report.state.accounts || []) {
    const row = document.createElement('div'); row.className = 'account'; row.textContent = account.alias + ' · ' + account.message; $('quota-refresh-accounts').append(row);
  }
}
for (const id of ['quota-refresh-enabled', 'quota-refresh-minutes']) $(id).addEventListener('input', () => { refreshDirty = true; });
action('save-quota-refresh', async () => {
  const minutes = $('quota-refresh-minutes');
  if (!minutes.reportValidity()) return;
  const interval = Number(minutes.value);
  if (!Number.isInteger(interval) || interval < 1 || interval > 10080) throw new Error('请输入 1–10080 之间的整数分钟');
  await api('config', { quotaRefreshEnabled: $('quota-refresh-enabled').checked, quotaRefreshMinutes: interval });
  refreshDirty = false; await readConfig(); status('刷新设置已保存');
});
action('refresh-quota-now', async () => { const r = await api('quota-refresh'); renderRefresh(r.refresh); status(r.message); });
action('search-city', async () => {
  status('正在搜索城市…'); cities = (await api('cities', { query: $('city-query').value })).cities;
  $('city-results').replaceChildren(); for (let i = 0; i < cities.length; i++) { const o = document.createElement('option'); o.value = String(i); o.textContent = cities[i].name + ' (' + cities[i].timezone + ')'; $('city-results').append(o); }
  status(cities.length ? '请选择城市后保存' : '未找到城市，可尝试拼音或英文名');
});
action('save-city', async () => { const city = cities[Number($('city-results').value)]; if (!city) throw new Error('请先搜索并选择城市'); const r = await api('config', { city }); await readConfig(); status(r.message); });
action('save-cpamp', async () => {
  const key = $('management-key').value;
  const r = await api('config', { cpampUrl: $('cpamp-url').value, ...(key ? { managementKey: key } : {}) });
  $('management-key').value = ''; discovered = []; orderDirty = false; $('accounts').replaceChildren(); await readConfig(); status(r.message);
});
action('clear-cpamp', async () => { const r = await api('config', { clearManagementKey: true, accounts: [] }); $('management-key').value = ''; discovered = []; orderDirty = false; $('accounts').replaceChildren(); await readConfig(); status(r.message); });
action('discover-accounts', async () => {
  status('正在读取账号列表…'); discovered = (await api('accounts')).accounts; $('accounts').replaceChildren();
  for (const account of discovered) {
    const box = document.createElement('div'); box.className = 'account'; const row = document.createElement('label'); row.className = 'row';
    const check = document.createElement('input'); check.type = 'checkbox'; check.checked = account.selected; check.dataset.accountId = account.id;
    const text = document.createElement('span'); text.textContent = account.provider;
    const alias = document.createElement('input'); alias.type = 'text'; alias.value = account.alias; alias.maxLength = 60; alias.setAttribute('aria-label', '显示别名'); alias.dataset.aliasId = account.id;
    row.append(check, text, alias); const label = document.createElement('small'); label.textContent = account.label; box.append(row, label); $('accounts').append(box);
  }
  status(discovered.length ? '选择关注账号，可修改壁纸显示别名' : '没有发现支持配额快照的账号');
});
action('save-accounts', async () => {
  if (!discovered.length) throw new Error('请先读取账号列表');
  const accounts = discovered.filter(a => document.querySelector(`[data-account-id="${a.id}"]`).checked).map(a => ({ id: a.id, alias: document.querySelector(`[data-alias-id="${a.id}"]`).value }));
  const order = new Map(accountOrder.map((a, index) => [a.id, index]));
  accounts.sort((a, b) => (order.get(a.id) ?? accountOrder.length) - (order.get(b.id) ?? accountOrder.length));
  const r = await api('config', { accounts }); orderDirty = false; await readConfig(); status(r.message);
});
action('load-sensors', async () => {
  const sensors = (await api('sensors')).sensors;
  for (const id of ['gpu-id', 'cpu-temp', 'gpu-temp']) while ($(id).options.length > 1) $(id).remove(1);
  const add = (id, value, text) => { const o = document.createElement('option'); o.value = value; o.textContent = text; $(id).append(o); };
  const seen = new Set();
  for (const s of sensors) {
    if (/^Gpu/.test(s.hardwareType) && !seen.has(s.hardwareId)) { seen.add(s.hardwareId); add('gpu-id', s.hardwareId, s.hardware); }
    if (s.type !== 'Temperature') continue;
    const label = `${s.hardware} · ${s.name} (${s.value === null ? '不可用' : s.value.toFixed(1) + '°C'})`;
    if (s.hardwareType === 'Cpu') add('cpu-temp', s.id, label);
    if (/^Gpu/.test(s.hardwareType)) add('gpu-temp', s.id, label);
  }
  for (const [id, key] of Object.entries({ 'gpu-id': 'gpuId', 'cpu-temp': 'cpuTemperatureId', 'gpu-temp': 'gpuTemperatureId' })) $(id).value = config.sensors[key] || '';
  $('sensor-report').textContent = sensors.filter(s => ['Load', 'Temperature', 'Data', 'SmallData'].includes(s.type)).map(s => `${s.hardware} / ${s.name}: ${s.value === null ? '不可用' : s.value.toFixed(2)} (${s.type})`).join('\n');
  status('传感器已检测');
});
action('save-sensors', async () => { const r = await api('config', { sensors: { gpuId: $('gpu-id').value || null, cpuTemperatureId: $('cpu-temp').value || null, gpuTemperatureId: $('gpu-temp').value || null } }); await readConfig(); status(r.message); });
action('copy-ticket', async () => { await navigator.clipboard.writeText(config.readToken); status('展示票据已复制'); });
action('toggle-ticket', async () => { $('ticket').type = $('ticket').type === 'password' ? 'text' : 'password'; });
action('refresh', async () => { const r = await api('refresh'); await readConfig(); status(r.message); });
(async () => {
  try {
    if (!['127.0.0.1', 'localhost', '[::1]'].includes(location.hostname)) throw new Error('本机设置只能从桥接服务打开');
    session = (await api('session')).session; await readConfig(); status('已连接。设置会保存在本机。');
    let polling = false;
    setInterval(async () => {
      if (document.hidden || polling) return;
      polling = true;
      try { renderRefresh(await api('quota-refresh/status')); } catch { $('quota-refresh-summary').textContent = '暂时无法读取刷新状态，请检查本机桥接'; }
      finally { polling = false; }
    }, 3000);
  } catch (e) { status(e.message); }
})();
