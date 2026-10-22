import type { QuotaAccount } from './contract';
import { AccountActivityControl, activityRanges, availableResets, compactCount } from './account-activity';

const el = <K extends keyof HTMLElementTagNameMap>(tag: K, cls = '', value?: string) => { const n = document.createElement(tag); n.className = cls; if (value !== undefined) n.textContent = value; return n; };
const icons: Record<string, string> = {
  requests: 'M4 8h16v12H4z M7 8l2-4h6l2 4 M4 13h5l2 3h2l2-3h5',
  success: 'M5 12l4 4L19 6',
  tokens: 'M6 4v6H3V4z M14 4h3v6h-3z M6 14v6H3v-6z M14 14h3v6h-3z',
  cost: 'M12 3v18 M17 6H9a3 3 0 000 6h6a3 3 0 010 6H6',
  last: 'M3 17l6-6 4 3 7-9 M15 5h5v5',
  reset: 'M20 7v5h-5 M20 12a8 8 0 10-2 6',
  close: 'M7 7l10 10 M17 7L7 17',
  up: 'M6 15l6-6 6 6',
  down: 'M6 9l6 6 6-6',
};
export function activityIcon(name: string) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(svg.namespaceURI, 'path'); path.setAttribute('d', icons[name]); svg.append(path); return svg;
}
const time = (at: string | null | undefined, zone: string, seconds = false) => at && Number.isFinite(Date.parse(at)) ? new Intl.DateTimeFormat('zh-CN', { timeZone: zone, month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', ...(seconds ? { second: '2-digit' } : {}), hour12: false }).format(new Date(at)) : '—';
const seconds = (ms: number | null | undefined) => ms !== null && ms !== undefined && Number.isFinite(ms) && ms >= 0 ? `${(ms / 1000).toFixed(1)}s` : '—';

const scrollCleanup = new WeakMap<HTMLElement, () => void>();
export function disposeRequestActivity(body: HTMLElement) { scrollCleanup.get(body)?.(); scrollCleanup.delete(body); }

/** WE forwards hover and release, but desktop wheel/drag input may never reach CEF. */
function requestScrollControls(body: HTMLElement, wrap: HTMLElement, controls: HTMLElement) {
  const up = el('button', 'activity-scroll-button'), down = el('button', 'activity-scroll-button');
  up.type = down.type = 'button';
  up.setAttribute('aria-label', '向上滚动请求'); down.setAttribute('aria-label', '向下滚动请求');
  up.title = '向上滚动 · 点击翻动，悬停连续滚动'; down.title = '向下滚动 · 点击翻动，悬停连续滚动';
  up.append(activityIcon('up')); down.append(activityIcon('down')); controls.append(up, down);
  let frame = 0, delay = 0, previous = 0;
  const active = () => wrap.isConnected && wrap.closest<HTMLElement>('.wallpaper')?.dataset.paused !== 'true';
  const stop = () => { clearTimeout(delay); cancelAnimationFrame(frame); frame = delay = previous = 0; };
  const update = () => {
    const max = wrap.scrollHeight - wrap.clientHeight;
    controls.hidden = max <= 1; up.disabled = wrap.scrollTop <= 1; down.disabled = wrap.scrollTop >= max - 1;
  };
  for (const [button, direction] of [[up, -1], [down, 1]] as const) {
    button.dataset.activityFocus = direction < 0 ? 'scroll-up' : 'scroll-down';
    button.onclick = () => {
      stop(); if (!active()) return;
      wrap.scrollBy({ top: direction * Math.max(1, wrap.clientHeight * .8), behavior: 'instant' }); update();
    };
    button.addEventListener('pointerenter', () => {
      stop(); update(); if (button.disabled || !active()) return;
      delay = window.setTimeout(() => {
        const step = (now: number) => {
          if (!active() || button.disabled || !button.matches(':hover')) { stop(); return; }
          const elapsed = previous ? Math.min(50, now - previous) : 0; previous = now;
          wrap.scrollTop += direction * Math.max(100, wrap.clientHeight * .9) * elapsed / 1000;
          update(); frame = requestAnimationFrame(step);
        };
        frame = requestAnimationFrame(step);
      }, 400);
    });
    button.addEventListener('pointerleave', stop);
  }
  wrap.addEventListener('scroll', update, { passive: true });
  window.addEventListener('blur', stop);
  const observer = new ResizeObserver(update); observer.observe(wrap);
  scrollCleanup.set(body, () => { stop(); observer.disconnect(); window.removeEventListener('blur', stop); });
  // Layout and the restored scroll position settle after the body is populated.
  update();
}

export function renderRequestActivity(body: HTMLElement, account: QuotaAccount, activity: AccountActivityControl, zone: string) {
  const { data, loading, message } = activity;
  const scope = JSON.stringify([activity.range, activity.customStart, activity.customEnd, activity.page]);
  const scroll = body.dataset.activityScope === scope ? body.querySelector('.request-table-scroll')?.scrollTop || 0 : 0;
  body.dataset.activityScope = scope;
  const keepFocus = body.contains(document.activeElement);
  const focus = keepFocus && document.activeElement instanceof HTMLElement ? document.activeElement.dataset.activityFocus : undefined;
  const rangeOpen = body.querySelector('.activity-period')?.getAttribute('aria-expanded') === 'true';
  disposeRequestActivity(body);
  body.replaceChildren();
  const topline = el('div', 'activity-topline');
  // Native select painting crashes this WE windowed CEF host. Keep its menu in the page.
  const control = el('div', 'activity-range-control'), range = el('button', 'activity-period', activityRanges.find(([value]) => value === activity.range)![1]);
  range.type = 'button'; range.setAttribute('role', 'combobox'); range.setAttribute('aria-label', '请求时间范围'); range.dataset.activityFocus = 'range'; range.dataset.value = activity.range;
  range.setAttribute('aria-haspopup', 'listbox'); range.setAttribute('aria-controls', 'activity-range-options');
  const options = el('div', 'activity-range-options'); options.id = 'activity-range-options'; options.setAttribute('role', 'listbox'); options.setAttribute('aria-label', '请求时间范围选项');
  const setOpen = (open: boolean) => { range.setAttribute('aria-expanded', String(open)); options.hidden = !open; };
  setOpen(rangeOpen);
  for (const [value, label] of activityRanges) {
    const option = el('button', 'activity-range-option', label); option.type = 'button'; option.tabIndex = -1; option.setAttribute('role', 'option'); option.setAttribute('aria-selected', String(value === activity.range)); option.dataset.activityFocus = `range-${value}`;
    option.onclick = () => { setOpen(false); range.focus({ preventScroll: true }); activity.selectRange(value); };
    options.append(option);
  }
  const focusOption = (index: number) => (options.children[(index + options.children.length) % options.children.length] as HTMLElement).focus({ preventScroll: true });
  range.onclick = () => { setOpen(options.hidden); if (!options.hidden) focusOption(activityRanges.findIndex(([value]) => value === activity.range)); };
  range.onkeydown = event => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); setOpen(true); focusOption(activityRanges.findIndex(([value]) => value === activity.range)); }
  };
  control.onkeydown = event => {
    if (options.hidden) return;
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); setOpen(false); range.focus({ preventScroll: true }); }
    else if (event.target !== range && ['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
      event.preventDefault(); const index = [...options.children].indexOf(document.activeElement!);
      focusOption(event.key === 'Home' ? 0 : event.key === 'End' ? options.children.length - 1 : index + (event.key === 'ArrowDown' ? 1 : -1));
    } else if (event.key === 'Tab') setOpen(false);
  };
  control.addEventListener('focusout', event => { if (!control.contains(event.relatedTarget as Node | null)) setOpen(false); });
  body.onclick = event => { if (!control.contains(event.target as Node)) setOpen(false); };
  range.title = `与 CPAMP 请求监控时间范围一致 · ${activity.timezone}`;
  control.append(range, options); topline.append(el('span', 'activity-section-title', '请求活动总览'), control);
  body.append(topline);
  if (activity.range === 'custom') {
    const form = el('form', 'activity-custom-range'); form.setAttribute('aria-label', '自定义请求时间范围');
    const start = el('input'), end = el('input'); start.type = end.type = 'datetime-local';
    start.setAttribute('aria-label', '开始时间'); end.setAttribute('aria-label', '结束时间'); start.required = end.required = true;
    start.dataset.activityFocus = 'custom-start'; end.dataset.activityFocus = 'custom-end';
    start.value = activity.customStart; end.value = activity.customEnd;
    start.oninput = () => { activity.customStart = start.value; }; end.oninput = () => { activity.customEnd = end.value; };
    const apply = el('button', 'activity-reload', '应用'); apply.type = 'submit'; apply.dataset.activityFocus = 'apply';
    const startLabel = el('label', '', '开始'), endLabel = el('label', '', '结束'); startLabel.append(start); endLabel.append(end);
    form.append(startLabel, endLabel, apply); form.title = `起止时间按 ${activity.timezone} 解释`;
    form.onsubmit = event => { event.preventDefault(); activity.applyCustom(); };
    if (activity.rangeError) { const error = el('span', 'activity-range-error', activity.rangeError); error.setAttribute('role', 'alert'); form.append(error); }
    body.append(form);
  }
  const summary = el('div', 'activity-overview');
  const values = [compactCount(data?.requests), data?.successRate !== null && data?.successRate !== undefined ? `${data.successRate.toFixed(1)}%` : '—', compactCount(data?.tokens), data?.estimatedCost !== null && data?.estimatedCost !== undefined ? `$${data.estimatedCost.toFixed(2)}` : '—', time(data?.lastActivity, zone)];
  ['请求数', '成功率', 'Token', '预估费用', '最后活动'].forEach((label, index) => {
    const card = el('div', 'activity-metric'); card.dataset.metric = ['requests', 'success', 'tokens', 'cost', 'last'][index];
    const heading = el('span', 'activity-metric-label'), icon = el('span', 'activity-metric-icon'); icon.append(activityIcon(card.dataset.metric)); heading.append(icon, el('span', '', label));
    card.append(heading, el('strong', 'activity-metric-value', values[index]));
    if (index === 3) card.title = 'CPAMP 按模型价格计算的预估成本（USD），不等同于 Provider 实际账单';
    if (index === 0) card.title = data?.requests === null || !data ? '尚无统计' : `${data.requests.toLocaleString('en-US')} 次请求`;
    if (index === 1 && data?.successRate !== undefined && data?.successRate !== null) card.dataset.warning = String(data.successRate < 95);
    summary.append(card);
  });
  body.append(summary);
  const heading = el('div', 'activity-table-heading'), update = el('button', 'activity-reload', loading ? '更新中…' : '更新活动'); update.type = 'button'; update.disabled = loading; update.onclick = () => activity.retry(); update.dataset.activityFocus = 'reload';
  const count = data?.totalCount ?? data?.requests;
  const pageLabel = data?.recent.length ? `${activity.page * 20 + 1}–${activity.page * 20 + data.recent.length}${count !== null && count !== undefined ? ` / ${compactCount(count)}` : ''} 条` : '';
  const scrollControls = el('div', 'activity-scroll-controls'); scrollControls.setAttribute('role', 'group'); scrollControls.setAttribute('aria-label', '请求列表滚动');
  heading.append(el('span', 'activity-section-title', '请求详情'), el('span', 'activity-recent-count', pageLabel), scrollControls, update); body.append(heading);
  const wrap = el('div', 'request-table-scroll'); wrap.tabIndex = 0; wrap.setAttribute('aria-label', '最近请求详情，可滚动查看');
  if (data?.recent.length) {
    const table = el('table', 'request-activity-table'); table.setAttribute('aria-label', `${account.alias}的请求详情`);
    const head = el('thead'), headRow = el('tr'); ['时间', '模型', '状态', '输入 / 输出', '总 Token', '首字 / 耗时'].forEach(label => { const cell = el('th', '', label); cell.scope = 'col'; headRow.append(cell); }); head.append(headRow); table.append(head);
    const rows = el('tbody');
    for (const request of data.recent) {
      const row = el('tr'), at = el('td', 'request-time', time(request.time, zone, true)), model = el('td', 'request-model', request.model || '未知模型'); model.title = request.model;
      const statusCell = el('td'), status = el('span', 'request-status', request.success === true ? '成功' : request.success === false ? `失败${request.statusCode ? ` ${request.statusCode}` : ''}` : '未知'); status.dataset.state = String(request.success); statusCell.append(status);
      const mix = el('td', 'request-token-mix', `${compactCount(request.inputTokens)} / ${compactCount(request.outputTokens)}`); mix.title = `输入 ${request.inputTokens ?? '未知'} / 输出 ${request.outputTokens ?? '未知'} / 缓存读取 ${request.cachedTokens ?? '未知'}`;
      const tokens = el('td', '', compactCount(request.tokens)); tokens.title = request.tokens === null ? '未知' : String(request.tokens);
      const latency = el('td', 'request-latency', `${seconds(request.ttftMs)} / ${seconds(request.durationMs)}`);
      latency.title = `首字响应时间 ${seconds(request.ttftMs)} / 请求总耗时 ${seconds(request.durationMs)}`;
      row.append(at, model, statusCell, mix, tokens, latency); rows.append(row);
    }
    table.append(rows); wrap.append(table);
  } else wrap.append(el('div', 'activity-empty', loading ? '正在读取该账号的请求活动…' : activity.range === 'custom' && !data ? '选择起止时间并点击“应用”' : data?.requests === 0 ? '所选范围暂无已记录请求' : message || data?.message || '暂无可用请求详情'));
  body.append(wrap); wrap.scrollTop = scroll;
  if (data?.hasMore || activity.page > 0) {
    const paging = el('nav', 'activity-paging'); paging.setAttribute('aria-label', '请求详情分页');
    const previous = el('button', 'activity-reload', '较新请求'), next = el('button', 'activity-reload', '较早请求'); previous.type = next.type = 'button';
    previous.disabled = loading || activity.page === 0; next.disabled = loading || !data?.hasMore || !data?.nextBeforeMs || !data?.nextBeforeId;
    previous.dataset.activityFocus = 'previous'; next.dataset.activityFocus = 'next';
    previous.onclick = () => activity.turnPage(-1); next.onclick = () => activity.turnPage(1);
    paging.append(el('span', 'activity-page-number', `第 ${activity.page + 1} 页`), previous, next); body.append(paging);
  }
  const stale = data?.status === 'stale' || !!message || !!data?.fetchedAt && Date.now() - Date.parse(data.fetchedAt) > 120000;
  const note = el('p', 'activity-source-note', message || (stale ? '显示上次活动 · ' : '') + `CPAMP 预估费用（USD） · ${data?.fetchedAt ? time(data.fetchedAt, zone) + ' 更新' : '等待读取'}`);
  if (data) note.title = `查询范围：${time(data.from, activity.timezone, true)} 至 ${time(data.to, activity.timezone, true)} · ${activity.timezone}`;
  body.append(note);
  requestScrollControls(body, wrap, scrollControls);
  if (keepFocus) {
    const target = focus ? body.querySelector<HTMLElement>(`[data-activity-focus="${focus}"]`) : null;
    (target && !target.matches(':disabled') ? target : wrap).focus({ preventScroll: true });
  }
}
export function updateResetBadge(badge: HTMLElement, account: QuotaAccount, now: number) {
  badge.hidden = account.provider !== 'codex';
  const count = availableResets(account.resetCredits, now);
  const label = count === null ? 'Codex 可用重置次数暂无可靠数据' : `Codex 可用重置次数：${count} 次`;
  badge.setAttribute('aria-label', label); badge.title = label; badge.dataset.unknown = String(count === null);
  badge.querySelector('span')!.textContent = count === null ? '—' : String(count);
}
