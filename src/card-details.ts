import { cardInsights, forecastHours, sampleStats, type HardwareInsightModel, type QuotaInsightModel, type WeatherInsightModel } from './insights';
import { AccountActivityControl, availableResets } from './account-activity';
import { activityIcon, disposeRequestActivity, renderRequestActivity, updateResetBadge } from './request-activity-view';
import { percent, temperature, weatherLabel } from './format';
const el = <K extends keyof HTMLElementTagNameMap>(tag: K, className = '', value?: string) => { const node = document.createElement(tag); node.className = className; if (value !== undefined) node.textContent = value; return node; };
const text = (node: Element, value: string) => { if (node.textContent !== value) node.textContent = value; };
const stamp = (at: string | number | null, timezone: string, full = false) => at !== null && Number.isFinite(new Date(at).getTime()) ? new Intl.DateTimeFormat('zh-CN', { timeZone: timezone, ...(full ? { month: '2-digit', day: '2-digit' } : {}), hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }).format(new Date(at)) : '未知';

/** Floating glass stays anchored to its widget, preserving the portrait gap. */
export class CardDetails {
  private target?: HTMLElement;
  private panel?: HTMLElement;
  private signature = '';
  private selectedSample?: number;
  private settingsLink?: HTMLAnchorElement;
  private region?: HTMLElement;
  private layer: HTMLElement;
  private animations: Animation[] = [];
  private closing = new Map<HTMLElement, Animation>();
  private activity = new AccountActivityControl(() => { this.signature = ''; this.sync(); });
  private activitySignature = '';
  private keyboardNavigation = false;
  private placementDirty = true;
  constructor(private root: HTMLElement, private bridgeUrl: () => string, private readToken: () => string) {
    this.layer = el('div', 'detail-layer'); root.append(this.layer);
    new ResizeObserver(() => { this.invalidatePlacement(); this.place(); }).observe(root);
    const regionObserver = new ResizeObserver(() => { this.invalidatePlacement(); this.place(); });
    root.querySelectorAll<HTMLElement>('.top,.quota,.hardware').forEach(region => regionObserver.observe(region));
    root.addEventListener('quota-layout-change', () => { if (this.target?.closest('.quota')) this.close(false); });
    // WE may forward only mouseup; do not let CEF's focus-visible heuristic
    // mistake that mouse activation for keyboard navigation.
    for (const type of ['pointerdown', 'mousedown', 'mouseup']) root.addEventListener(type, () => { this.keyboardNavigation = false; }, true);
    root.addEventListener('keydown', event => { if (!['Control', 'Shift', 'Alt', 'Meta'].includes(event.key)) this.keyboardNavigation = true; }, true);
    root.addEventListener('click', event => {
      const target = event.target instanceof Element ? event.target : undefined;
      if (target?.closest('button,a,.card-details')) return;
      const card = target?.closest<HTMLElement>('[data-detail-title]');
      if (card) this.open(card); else if (this.panel) this.close();
    });
    root.addEventListener('keydown', event => {
      if (event.key === 'Escape' && this.panel) { event.preventDefault(); this.close(); }
      if ((event.key === 'Enter' || event.key === ' ') && event.target instanceof HTMLElement && event.target.matches('[data-detail-title]')) { event.preventDefault(); this.open(event.target); }
    });
  }
  sync() {
    if (!this.target || !this.panel) return;
    if (!this.target.isConnected || this.target.closest('.widget-hidden')) { this.close(false); return; }
    const model = cardInsights.get(this.target);
    if (!model) { this.close(false); return; }
    const now = Date.now();
    if (this.placementDirty) this.place();
    if (model.kind === 'quota') this.activity.configure(model.accountId, this.bridgeUrl(), this.readToken(), this.root.dataset.paused !== 'true');
    if (this.root.dataset.dynamic !== 'true') {
      this.animations.forEach(animation => animation.cancel()); this.animations = [];
      for (const [panel, animation] of this.closing) { animation.cancel(); panel.remove(); } this.closing.clear();
    }
    const stale = model.kind === 'hardware' ? !model.observedAt || now - model.observedAt > 10000 : model.kind === 'weather' ? model.data.status !== 'ok' || !model.data.fetchedAt || now - Date.parse(model.data.fetchedAt) > 1800000 : model.data.status !== 'ok';
    if (this.settingsLink) this.settingsLink.href = this.bridgeUrl() + (model.kind === 'weather' ? '/admin/#city-query' : '/admin/#account-order-heading');
    const timeState = model.kind === 'quota' ? availableResets(model.data.accounts.find(a => a.id === model.accountId)?.resetCredits, now) : model.kind === 'weather' ? [stale, forecastHours(model.data.hourly, now).map(h => h.time)] : stale;
    const signature = JSON.stringify([model, timeState, this.activity.data, this.activity.loading, this.activity.message, this.activity.range, this.activity.rangeError, this.activity.page]);
    if (signature === this.signature) return;
    this.signature = signature;
    text(this.panel.querySelector('h2')!, model.title); this.panel.setAttribute('aria-label', model.title);
    const body = this.panel.querySelector<HTMLElement>('.card-details-body')!;
    if (model.kind === 'quota') this.quota(body, model, now);
    else if (model.kind === 'hardware') this.hardware(body, model, stale);
    else this.weather(body, model, stale, now);
  }
  close(restoreFocus = true, immediate = false) {
    this.activity.close();
    this.activitySignature = '';
    const target = this.target, panel = this.panel; target?.setAttribute('aria-expanded', 'false');
    this.region?.removeAttribute('data-details-open');
    if (panel) {
      const body = panel.querySelector<HTMLElement>('.card-details-body');
      if (body) disposeRequestActivity(body);
      // Start the reverse motion at the current visible transform, even mid-opening.
      const style = getComputedStyle(panel), transform = style.transform, opacity = style.opacity;
      this.animations.forEach(animation => animation.cancel()); this.animations = [];
      panel.removeAttribute('role'); panel.setAttribute('aria-hidden', 'true'); panel.inert = true; panel.classList.add('detail-closing');
      if (!immediate && this.root.dataset.dynamic === 'true') {
        const u = parseFloat(getComputedStyle(this.root).getPropertyValue('--u')) || 1;
        const animation = panel.animate([{ opacity, transform }, { opacity: 0, transform: `translateY(${8 * u}px) scale(.95)` }], { duration: 190, easing: 'cubic-bezier(.4,0,.8,.5)', fill: 'forwards' });
        this.closing.set(panel, animation);
        void animation.finished.catch(() => {}).then(() => { panel.remove(); this.closing.delete(panel); });
      } else panel.remove();
    }
    this.panel = this.target = this.region = undefined; this.signature = ''; this.selectedSample = undefined; this.settingsLink = undefined;
    if (restoreFocus && this.keyboardNavigation && target?.isConnected) target.focus({ preventScroll: true });
    else if (target === document.activeElement) target?.blur();
  }
  private open(card: HTMLElement) {
    if (this.target === card) { this.close(); return; }
    const model = cardInsights.get(card); if (!model) return;
    this.close(false, true);
    for (const [closing, animation] of this.closing) { animation.cancel(); closing.remove(); } this.closing.clear();
    const region = card.closest<HTMLElement>('.quota,.hardware,.top'); if (!region) return;
    this.region = region; region.dataset.detailsOpen = 'true';
    this.target = card; card.setAttribute('aria-expanded', 'true');
    const panel = el('section', 'card-details'); panel.dataset.kind = model.kind; panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-label', model.title);
    panel.append(el('span', 'detail-optics'));
    const header = el('div', 'card-details-heading'), title = el('h2');
    const close = el('button', 'details-close'); close.append(activityIcon('close')); close.type = 'button'; close.setAttribute('aria-label', '关闭详情'); close.title = '关闭详情'; close.onclick = () => this.close();
    header.append(title, close); panel.append(header, el('div', 'card-details-body'));
    if (model.kind === 'quota') {
      const badge = el('span', 'reset-credit-badge'); badge.tabIndex = 0; badge.setAttribute('role', 'img'); badge.append(activityIcon('reset'), el('span')); header.insertBefore(badge, close);
      this.settingsLink = el('a', 'details-action', '账号设置'); this.settingsLink.target = '_blank'; this.settingsLink.rel = 'noopener noreferrer'; header.insertBefore(this.settingsLink, close);
    } else if (model.kind === 'weather') {
      this.settingsLink = el('a', 'details-action', '切换城市'); this.settingsLink.target = '_blank'; this.settingsLink.rel = 'noopener noreferrer'; header.insertBefore(this.settingsLink, close);
    }
    this.layer.append(panel); this.panel = panel; this.placementDirty = true; this.sync(); close.focus({ preventScroll: true });
    let lightAt = 0;
    panel.addEventListener('pointermove', event => {
      if (this.root.dataset.dynamic !== 'true' || performance.now() - lightAt < 1000 / 30) return;
      lightAt = performance.now(); const bounds = panel.getBoundingClientRect();
      panel.style.setProperty('--detail-light-x', `${(event.clientX - bounds.left - bounds.width / 2) * .055}px`);
      panel.style.setProperty('--detail-light-y', `${(event.clientY - bounds.top - bounds.height / 2) * .045}px`);
    });
    const origin = card.getBoundingClientRect(), bounds = panel.getBoundingClientRect(), rootBounds = this.root.getBoundingClientRect(), ratio = this.root.clientWidth / Math.max(1, rootBounds.width);
    panel.style.transformOrigin = `${Math.max(0, Math.min(bounds.width, origin.left + origin.width / 2 - bounds.left)) * ratio}px ${Math.max(0, Math.min(bounds.height, origin.top + origin.height / 2 - bounds.top)) * ratio}px`;
    if (this.root.dataset.dynamic === 'true') {
      const u = parseFloat(getComputedStyle(this.root).getPropertyValue('--u')) || 1;
      this.animations = [
        panel.animate([{ opacity: 0, transform: `translateY(${12 * u}px) scale(.9)` }, { opacity: 1, transform: 'translateY(0) scale(1.006)', offset: .72 }, { opacity: 1, transform: 'none' }], { duration: 380, easing: 'cubic-bezier(.16,1,.3,1)' }),
        panel.querySelector('.card-details-body')!.animate([{ opacity: 0, transform: `translateY(${7 * u}px)` }, { opacity: 1, transform: 'none' }], { duration: 240, delay: 65, fill: 'backwards', easing: 'cubic-bezier(.2,.8,.2,1)' }),
      ];
    }
  }
  invalidatePlacement() { this.placementDirty = true; }
  private place() {
    if (!this.panel || !this.region?.isConnected) return;
    this.placementDirty = false;
    const root = this.root.getBoundingClientRect(), region = this.region.getBoundingClientRect(), sx = this.root.clientWidth / Math.max(1, root.width), sy = this.root.clientHeight / Math.max(1, root.height);
    const u = parseFloat(getComputedStyle(this.root).getPropertyValue('--u')) || 1;
    const material = getComputedStyle(this.region);
    for (const name of ['--glass-alpha', '--glass-blur', '--glass-edge']) this.panel.style.setProperty(name, material.getPropertyValue(name));
    const inset = 7 * u, topInset = 6 * u;
    const left = Math.max(inset, (region.left - root.left) * sx + inset), top = Math.max(inset, (region.top - root.top) * sy + topInset);
    this.panel.style.left = `${left}px`; this.panel.style.top = `${top}px`;
    this.panel.style.width = `${Math.max(1, Math.min(region.width * sx - inset * 2, this.root.clientWidth - left - inset))}px`;
    this.panel.style.height = `${Math.max(1, Math.min(region.height * sy - topInset - inset, this.root.clientHeight - top - inset))}px`;
  }
  private weather(body: HTMLElement, model: WeatherInsightModel, stale: boolean, now: number) {
    const hours = forecastHours(model.data.hourly, now);
    const key = hours.map(h => h.time).join(',');
    if (!hours.length) {
      delete body.dataset.hours;
      body.replaceChildren(el('div', 'forecast-empty', model.data.fetchedAt ? '小时预报暂不可用，等待下一次天气更新' : '正在获取小时预报'));
      body.append(el('p', 'insight-note', '当前条件仍保留；可检查本机桥接与天气连接。')); return;
    }
    if (body.dataset.hours !== key) {
      body.replaceChildren(); body.dataset.hours = key;
      body.append(el('p', 'forecast-summary'));
      const grid = el('div', 'forecast-grid'); grid.style.setProperty('--forecast-count', String(hours.length));
      for (const hour of hours) {
        const slot = el('div', 'forecast-hour'); slot.dataset.time = hour.time;
        slot.append(el('span', 'forecast-time'), el('strong', 'forecast-temperature'), el('div', 'forecast-rain-track'), el('span', 'forecast-probability'), el('span', 'forecast-amount'));
        slot.querySelector('.forecast-rain-track')!.append(el('i'));
        grid.append(slot);
      }
      body.append(grid, el('p', 'insight-note'));
    }
    const formatter = new Intl.DateTimeFormat('zh-CN', { timeZone: model.timezone, hour: '2-digit', hour12: false });
    const hourLabel = (time: string) => `${formatter.formatToParts(new Date(time)).find(part => part.type === 'hour')?.value || '—'}时`;
    const date = (time: string | number) => new Intl.DateTimeFormat('en-CA', { timeZone: model.timezone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(time));
    const today = date(now);
    const slots = body.querySelectorAll<HTMLElement>('.forecast-hour');
    hours.forEach((hour, index) => {
      const slot = slots[index], day = date(hour.time) === today ? '' : '明日 ';
      text(slot.querySelector('.forecast-time')!, `${day}${hourLabel(hour.time)}`);
      text(slot.querySelector('.forecast-temperature')!, temperature(hour.temperature, '°'));
      const probability = hour.precipitationProbability;
      text(slot.querySelector('.forecast-probability')!, probability === null || !Number.isFinite(probability) ? '—' : `${Math.round(Math.max(0, Math.min(100, probability)))}%`);
      text(slot.querySelector('.forecast-amount')!, hour.precipitationMm === null || !Number.isFinite(hour.precipitationMm) ? '— mm' : `${Math.max(0, hour.precipitationMm).toFixed(1)} mm`);
      const track = slot.querySelector<HTMLElement>('.forecast-rain-track')!;
      track.dataset.unknown = String(probability === null); track.firstElementChild!.setAttribute('style', `height:${Math.max(0, Math.min(100, probability ?? 0))}%`);
      slot.title = `${stamp(hour.time, model.timezone, true)}\n${weatherLabel(hour.code)}\n温度 ${temperature(hour.temperature)}\n降水概率 ${probability === null ? '未知' : percent(probability)}\n预计降水量 ${hour.precipitationMm === null ? '未知' : `${hour.precipitationMm} mm`}`;
    });
    const rain = hours.filter(h => h.precipitationProbability !== null && h.precipitationProbability >= 50 || h.precipitationMm !== null && h.precipitationMm > 0);
    const validProbability = hours.filter(h => h.precipitationProbability !== null && Number.isFinite(h.precipitationProbability));
    const summary = rain.length ? `${date(rain[0].time) === today ? '' : '明日 '}${hourLabel(rain[0].time)}起有降水信号` : validProbability.length === hours.length ? `这 ${hours.length} 个时段暂未出现明显降水信号` : '部分时段缺少降水预报';
    text(body.querySelector('.forecast-summary')!, stale ? '上次小时预报 · 天气连接待更新' : summary);
    text(body.querySelector('.insight-note')!, `降水概率 / 预计降水量 · ${model.timezone} · Open-Meteo 模型预报${stale ? ' · 数据已过期' : ''}`);
  }
  private quota(body: HTMLElement, model: QuotaInsightModel, now: number) {
    const account = model.data.accounts.find(a => a.id === model.accountId); if (!account) return;
    updateResetBadge(this.panel!.querySelector<HTMLElement>('.reset-credit-badge')!, account, now);
    const signature = JSON.stringify([account.alias, this.activity.data, this.activity.loading, this.activity.message, model.timezone, this.activity.range, this.activity.rangeError, this.activity.page]);
    if (signature === this.activitySignature) return;
    this.activitySignature = signature;
    renderRequestActivity(body, account, this.activity, model.timezone);
  }
  private hardware(body: HTMLElement, model: HardwareInsightModel, stale: boolean) {
    if (!body.querySelector('.insight-chart')) {
      const layout = el('div', 'hardware-insight-layout'), chart = el('div', 'insight-chart');
      const scale = el('div', 'chart-scale'); scale.append(el('span', '', '100%'), el('span', '', '50%'), el('span', '', '0%'));
      const plot = el('div', 'chart-plot'), svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      svg.setAttribute('viewBox', '0 0 600 90'); svg.setAttribute('preserveAspectRatio', 'none'); svg.setAttribute('tabindex', '0'); svg.setAttribute('role', 'img'); svg.classList.add('detail-trend');
      const times = el('div', 'chart-times'); times.append(el('span'), el('span'));
      const marker = el('span', 'chart-marker'); marker.hidden = true; marker.setAttribute('aria-hidden', 'true');
      plot.append(svg, marker, times); chart.append(scale, plot); layout.append(chart, el('dl', 'insight-stats')); body.append(layout, el('p', 'chart-readout'), el('p', 'insight-note'));
      svg.addEventListener('pointermove', event => {
        const current = this.target && cardInsights.get(this.target); if (current?.kind !== 'hardware' || !current.samples.length) return;
        const bounds = svg.getBoundingClientRect(), ratio = Math.max(0, Math.min(1, (event.clientX - bounds.left) / Math.max(1, bounds.width)));
        const first = current.samples[0].at, last = current.samples[current.samples.length - 1].at, at = first + ratio * (last - first);
        this.selectedSample = current.samples.reduce((nearest, s) => Math.abs(s.at - at) < Math.abs(nearest.at - at) ? s : nearest).at; this.readout(body, current);
      });
      svg.addEventListener('pointerleave', () => { this.selectedSample = undefined; const current = this.target && cardInsights.get(this.target); if (current?.kind === 'hardware') this.readout(body, current); });
      svg.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        const current = this.target && cardInsights.get(this.target); if (current?.kind !== 'hardware' || !current.samples.length) return;
        event.preventDefault(); const index = current.samples.findIndex(s => s.at === this.selectedSample);
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? current.samples.length - 1 : Math.max(0, Math.min(current.samples.length - 1, (index < 0 ? current.samples.length - 1 : index) + (event.key === 'ArrowLeft' ? -1 : 1)));
        this.selectedSample = current.samples[next].at; this.readout(body, current);
      });
    }
    const samples = model.samples, stats = sampleStats(samples), svg = body.querySelector<SVGSVGElement>('.detail-trend')!;
    const span = stats.span < 60 ? `${Math.round(stats.span)} 秒` : `${(stats.span / 60).toFixed(1)} 分钟`;
    const summary = `已记录 ${span}，${stats.count} 个有效样本，样本均值 ${percent(stats.average)}，峰值 ${percent(stats.peak)}`;
    svg.setAttribute('aria-label', `${model.title}；${summary}。左右方向键查看采样时点。`);
    const first = samples[0]?.at || 0, last = samples[samples.length - 1]?.at || 0;
    let run: string[] = []; const segments: string[] = [];
    const flush = () => { if (run.length) segments.push(run.join(' ')); run = []; };
    for (const sample of samples) {
      if (sample.value === null || !Number.isFinite(sample.value)) { flush(); continue; }
      const x = (sample.at - first) / Math.max(1, last - first) * 600, y = 90 - Math.max(0, Math.min(100, sample.value)) * .9;
      run.push(`${x.toFixed(2)},${y.toFixed(2)}`);
    }
    flush(); while (svg.children.length > segments.length) svg.lastElementChild!.remove();
    segments.forEach((points, index) => { let line = svg.children[index]; if (!line) { line = document.createElementNS(svg.namespaceURI, 'polyline'); svg.append(line); } line.setAttribute('points', points); });
    const times = body.querySelector('.chart-times')!; text(times.firstElementChild!, samples.length ? stamp(first, model.timezone) : '等待采样'); text(times.lastElementChild!, samples.length > 1 ? stamp(last, model.timezone) : '');
    const statsNode = body.querySelector('.insight-stats')!;
    const values = [['样本均值', percent(stats.average)], ['峰值 / 低值', `${percent(stats.peak)} / ${percent(stats.low)}`], model.index < 2 ? ['温度峰值', temperature(stats.temperaturePeak)] : ['当前可用', model.freeBytes === null ? '未知' : `${(model.freeBytes / 2 ** 30).toFixed(1)} GB`]];
    if (!statsNode.children.length) for (let i = 0; i < values.length; i++) { const row = el('div'); row.append(el('dt'), el('dd')); statsNode.append(row); }
    values.forEach(([label, value], i) => { text(statsNode.children[i].firstElementChild!, label); text(statsNode.children[i].lastElementChild!, value); });
    const note = stale || model.status !== 'ok' ? `采样已中断 · ${model.status !== 'ok' && model.message ? model.message : '等待本机数据恢复'}` : stats.count < 2 ? '正在积累真实采样，暂不能判断趋势' : `本次已记录 ${span} · ${stats.count} 个有效样本 · 最多保留最近 30 个采样`;
    text(body.querySelector('.insight-note')!, note); this.readout(body, model);
  }
  private readout(body: HTMLElement, model: HardwareInsightModel) {
    const sample = model.samples.find(s => s.at === this.selectedSample);
    const marker = body.querySelector<HTMLElement>('.chart-marker')!; marker.hidden = !sample;
    if (sample) marker.style.left = `${(sample.at - model.samples[0].at) / Math.max(1, model.samples[model.samples.length - 1].at - model.samples[0].at) * 100}%`;
    text(body.querySelector('.chart-readout')!, sample ? `${stamp(sample.at, model.timezone)} · 使用率 ${percent(sample.value)}${model.index < 2 ? ` · 温度 ${temperature(sample.temperature)}` : ''}` : '移到曲线上查看时点 · 键盘支持左右方向键');
  }
}
