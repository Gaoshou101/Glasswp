import type { HardwareData, QuotaData, QuotaWindow, WeatherData } from './contract';
import type { Settings } from './settings';
import { ageLabel, capacity, durationLabel, percent, quotaRemaining, resetText, temperature, weatherLabel } from './format';
import { nextQuotaStateChange, quotaPages, quotaWindowState, type QuotaEntry, type QuotaPage } from './quota';
import { setCardInsight, type HardwareSample } from './insights';
import { ClockFormats, clockNow } from './clock-format';
import { providerBrand, providerGlyph, providerNames, weatherIconMarkup, weatherKind } from './icons';

const el = <K extends keyof HTMLElementTagNameMap>(tag: K, className: string, value?: string): HTMLElementTagNameMap[K] => { const n = document.createElement(tag); n.className = className; if (value !== undefined) n.textContent = value; return n; };
const text = (node: Element, value: string) => { if (node.textContent !== value) node.textContent = value; };
/** "38%" renders as a large number with a smaller unit; textContent stays "38%". */
const unitValue = (node: Element, value: string) => {
  let number = node.querySelector<HTMLElement>(':scope > .value-number'), unit = node.querySelector<HTMLElement>(':scope > .value-unit');
  if (!number || !unit) node.replaceChildren(number = el('span', 'value-number'), unit = el('small', 'value-unit'));
  const match = /^(.*?)(%)$/.exec(value);
  text(number, match ? match[1] : value); text(unit, match ? match[2] : ''); unit.hidden = !match;
};
interface WindowView { block: HTMLElement; remainingLabel: HTMLElement; remaining: Text; fill: HTMLElement; track: HTMLElement; title: HTMLElement; meta: HTMLElement; countdown: { node: HTMLElement; window: QuotaWindow } }
interface AccountView { node: HTMLElement; mark: HTMLElement; alias: HTMLElement; plan: HTMLElement; windows: WindowView[]; freshness: { node: HTMLElement; entry: QuotaEntry }; empty?: HTMLElement }
export class ClockWeather {
  private weather?: WeatherData;
  private formats = new ClockFormats();
  private time: HTMLElement;
  private date: HTMLElement;
  private caption: HTMLElement;
  constructor(private root: HTMLElement, private settings: Settings) {
    this.time = root.querySelector<HTMLElement>('.time')!;
    this.date = root.querySelector<HTMLElement>('.date')!;
    this.caption = root.querySelector<HTMLElement>('.weather-caption')!;
    const icon = root.querySelector<SVGSVGElement>('.weather-icon')!;
    icon.innerHTML = weatherIconMarkup(null); icon.dataset.kind = 'unknown';
  }
  tick() {
    const time = this.time;
    time.dataset.seconds = String(this.settings.seconds); time.dataset.hour12 = String(this.settings.hour12);
    const formatted = this.formats.format(this.settings.timezone, this.settings.hour12, this.settings.seconds, clockNow(this.settings.offsetSeconds));
    text(time, formatted.time); text(this.date, formatted.date);
    const card = this.root.querySelector<HTMLElement>('.clock')!;
    setCardInsight(card, { kind: 'clock', title: '世界时钟', timezone: this.settings.timezone, worldZones: this.settings.worldZones, seconds: this.settings.seconds, hour12: this.settings.hour12, offsetSeconds: this.settings.offsetSeconds });
    card.title = `${this.settings.timezone} · 点击展开世界时钟`;
    if (this.weather) {
      const w = this.weather, expired = w.fetchedAt && Date.now() - Date.parse(w.fetchedAt) > 1800000;
      text(this.caption, w.city.split(' · ')[0] + (w.status === 'ok' && !expired ? '' : ' · 上次天气'));
    }
  }
  update(w: WeatherData) {
    const previous = this.weather; this.weather = w;
    text(this.root.querySelector('.temperature')!, temperature(w.temperature, '°'));
    text(this.root.querySelector('.weather-description')!, w.temperature === null ? w.message : weatherLabel(w.code));
    text(this.root.querySelector('.weather-range')!, `${temperature(w.low, '°')} / ${temperature(w.high, '°')}`);
    const icon = this.root.querySelector<SVGSVGElement>('.weather-icon')!;
    icon.style.opacity = w.temperature === null ? '.4' : '1';
    if (!previous || previous.code !== w.code || previous.isDay !== w.isDay) {
      icon.innerHTML = weatherIconMarkup(w.code, w.isDay); icon.dataset.kind = weatherKind(w.code);
      if (previous && this.root.dataset.dynamic === 'true') icon.animate([{ opacity: .4, transform: 'translateY(2px)' }, { opacity: w.temperature === null ? .4 : 1, transform: 'translateY(0)' }], { duration: 260, easing: 'cubic-bezier(.2,.8,.2,1)' });
    }
    this.root.querySelector('.weather')!.setAttribute('title', `${w.city}\n${w.message}\n${w.observedAt || ''}`);
    setCardInsight(this.root.querySelector<HTMLElement>('.weather')!, { kind: 'weather', title: `${w.city.split(' · ')[0]} · 天气预报`, data: w, timezone: w.timezone || this.settings.timezone });
    this.tick();
  }
}
export class QuotaComponent {
  private data: QuotaData = { status: 'unconfigured', message: '在本机设置中连接 CPAMP', fetchedAt: null, accounts: [] };
  private pages: QuotaPage[] = [];
  private index = 0;
  private rotationAt = Date.now();
  private expiresAt = Infinity;
  private countdowns: { node: HTMLElement; window: QuotaWindow }[] = [];
  private freshness: { node: HTMLElement; entry: QuotaEntry }[] = [];
  private views: AccountView[] = [];
  private structure = '';
  private pagingAnimation?: Animation;
  private navCount = -1;
  private pageLabel?: HTMLElement;
  constructor(private root: HTMLElement, private settings: Settings) { this.render(); }
  update(data: QuotaData) {
    const key = this.pages[this.index]?.key;
    this.data = data; this.pages = quotaPages(data.accounts, this.settings.quotaLayout, this.settings.ignoreCodexBaseWeekly, this.settings.ignoreCodexImageQuota);
    this.expiresAt = nextQuotaStateChange(this.pages.flatMap(p => p.entries.flatMap(e => e.windows)));
    const same = this.pages.findIndex(p => p.key === key);
    this.index = Math.min(this.index, Math.max(0, this.pages.length - 1));
    if (same >= 0) this.index = same;
    this.render();
  }
  refreshLayout() { this.index = 0; this.rotationAt = Date.now(); this.update(this.data); }
  tick() {
    const now = Date.now();
    if (now >= this.expiresAt) { this.update(this.data); return; }
    for (const item of this.countdowns) text(item.node, resetText(item.window, now));
    for (const { node, entry } of this.freshness) {
      // An exhausted window's old sample is still valid until its reset, so it never marks the card expired.
      const evidence = (entry.windows.length ? entry.windows : entry.account.windows).filter(w => quotaWindowState(w, now) !== 'exhausted');
      const observations = evidence.map(w => w.observedAt).filter((w): w is string => !!w).sort();
      const observed = observations[0] || null;
      const stale = ['offline', 'error', 'unauthorized', 'unsupported'].includes(this.data.status) || evidence.some(w => w.stale) || !observed || now - Date.parse(observed) > 900000;
      text(node, stale && observed ? '数据已过期' : '');
      node.title = observed ? `数据已过期 · ${ageLabel(observed, now)}\n采集时间：${observed}` : '';
      node.dataset.stale = String(stale);
      node.hidden = !stale || !observed;
    }
    const region = this.root.querySelector<HTMLElement>('.quota')!;
    const interacting = region.matches(':hover') || region.contains(document.activeElement) || region.dataset.detailsOpen === 'true';
    if (interacting) this.rotationAt = now;
    else if (this.settings.rotateSeconds > 0 && this.pages.length > 1 && now - this.rotationAt >= this.settings.rotateSeconds * 1000) this.change(1);
  }
  private change(delta: number) {
    if (!this.pages.length) return;
    this.index = (this.index + delta + this.pages.length) % this.pages.length; this.rotationAt = Date.now(); this.render();
    this.pagingAnimation?.cancel();
    if (this.root.dataset.dynamic === 'true') this.pagingAnimation = this.root.querySelector<HTMLElement>('#quota-content')!.animate([{ opacity: .35, transform: `translateX(${delta > 0 ? 10 : -10}px)` }, { opacity: 1, transform: 'translateX(0)' }], { duration: 280, easing: 'cubic-bezier(.22,.8,.25,1)' });
  }
  private render() {
    const content = this.root.querySelector<HTMLElement>('#quota-content')!;
    const secondary = this.root.querySelector('.secondary')!;
    const page = this.pages[this.index];
    const cards = this.settings.quotaLayout === 'cards';
    this.root.dataset.quotaLayout = this.settings.quotaLayout;
    this.root.dataset.quotaDense = String(page?.entries.some(e => e.windows.length > 2) ?? false);
    this.root.querySelector<HTMLElement>('.quota-heading')!.title = this.data.message;
    if (this.navCount !== this.pages.length) {
      this.navCount = this.pages.length; secondary.replaceChildren(); this.pageLabel = undefined;
      if (this.pages.length > 1) {
        const nav = el('div', 'quota-nav'); nav.setAttribute('aria-label', '额度分页');
        const prev = el('button', '', '‹'), next = el('button', '', '›'); prev.type = next.type = 'button'; prev.setAttribute('aria-label', '上一页额度'); next.setAttribute('aria-label', '下一页额度'); prev.onclick = () => this.change(-1); next.onclick = () => this.change(1);
        this.pageLabel = el('span', ''); nav.append(prev, this.pageLabel, next); secondary.append(nav);
      }
    }
    if (this.pageLabel) text(this.pageLabel, `${this.index + 1} / ${this.pages.length}`);
    const structure = JSON.stringify([cards, this.pages.length, page?.key, page?.entries.map(e => [e.account.id, e.windows.map(w => w.id)]), page ? null : [this.data.status, this.data.message]]);
    if (structure === this.structure && page) { this.views.forEach((view, i) => this.updateAccount(view, page.entries[i])); this.tick(); return; }
    this.structure = structure; this.countdowns = []; this.freshness = []; this.views = [];
    this.root.dispatchEvent(new Event('quota-layout-change'));
    content.replaceChildren();
    if (!page) {
      const block = el('div', 'empty-quota'); block.append(el('div', '', this.data.status === 'unconfigured' ? '连接你的 AI 套餐' : '暂无可显示的额度'), el('small', '', this.data.message)); content.append(block); return;
    }
    const grid = el('div', cards ? 'quota-cards' : 'quota-single');
    grid.dataset.cardCount = String(page.entries.length);
    for (const entry of page.entries) grid.append(this.account(entry, cards));
    content.append(grid);
    this.tick();
  }
  private account(page: QuotaEntry, cards: boolean): HTMLElement {
    const content = el('article', cards ? 'quota-card' : 'quota-account'); content.dataset.accountId = page.account.id;
    content.dataset.dense = String(page.windows.length > 2);
    content.dataset.windowCount = String(page.windows.length);
    content.setAttribute('aria-label', page.account.alias); content.title = page.account.alias;
    const heading = el('div', 'account-heading'), label = el('span', 'account-label');
    const alias = el('span', 'account-alias', page.account.alias), mark = el('span', 'account-mark');
    label.append(mark, alias);
    heading.append(label); content.append(heading);
    const plan = el('span', 'plan', page.account.plan || page.account.provider); plan.title = page.account.plan || page.account.provider;
    const meta = el('div', 'account-meta');
    const freshness = el('div', 'card-freshness'), freshnessView = { node: freshness, entry: page }; this.freshness.push(freshnessView);
    meta.append(plan, freshness); content.append(meta);
    const windows = el('div', 'windows');
    const windowViews: WindowView[] = [];
    for (const w of page.windows) {
      const block = el('div', 'quota-window'), line = el('div', 'window-line');
      const remaining = el('span', 'remaining'), remainingLabel = el('small', '', '剩余'), value = document.createTextNode(quotaRemaining(w)), title = el('span', 'window-title', w.label); remaining.append(remainingLabel, value);
      line.append(title, remaining);
      const track = el('div', 'track'), fill = el('div', 'fill'); track.append(fill);
      const countdown = el('div', 'reset', resetText(w)), countdownView = { node: countdown, window: w }; this.countdowns.push(countdownView);
      const units = w.usedValue !== null && w.limitValue !== null ? `${w.usedValue} / ${w.limitValue} ${w.quotaUnit}` : durationLabel(w.durationSeconds);
      const meta = el('div', 'window-meta', units || '周期未知');
      block.append(line, track, countdown, meta); windows.append(block);
      windowViews.push({ block, remainingLabel, remaining: value, fill, track, title, meta, countdown: countdownView });
    }
    content.append(windows);
    let empty: HTMLElement | undefined;
    if (!page.windows.length) {
      empty = el('div', 'empty-quota'); empty.style.height = 'calc(155 * var(--u))';
      const ignored = !!page.ignoredWindows && !page.account.windows.length;
      empty.append(el('div', '', ignored ? page.ignoredLabel || '额度窗口已忽略' : page.expiredWindows ? '暂无最新额度，等待刷新' : '暂无额度证据')); content.append(empty);
    }
    const glint = el('span', 'liquid-glint'); glint.setAttribute('aria-hidden', 'true'); content.prepend(glint);
    const view = { node: content, mark, alias, plan, windows: windowViews, freshness: freshnessView, empty }; this.views.push(view); this.updateAccount(view, page);
    return content;
  }
  private updateAccount(view: AccountView, page: QuotaEntry) {
    text(view.alias, page.account.alias); view.node.setAttribute('aria-label', page.account.alias); view.node.title = page.account.alias;
    if (view.mark.dataset.provider !== page.account.provider) {
      view.mark.dataset.provider = page.account.provider; view.mark.dataset.brand = providerBrand(page.account.provider); view.mark.title = providerNames[page.account.provider] || page.account.provider || '未知平台';
      view.mark.replaceChildren(providerGlyph(page.account.provider));
    }
    text(view.plan, page.account.plan || page.account.provider); view.plan.title = view.plan.textContent || '';
    view.freshness.entry = page;
    const absolute = (value: string | null) => value && Number.isFinite(Date.parse(value)) ? new Intl.DateTimeFormat('zh-CN', { timeZone: this.settings.timezone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }).format(new Date(value)) : '未知';
    const now = Date.now();
    view.windows.forEach((item, i) => {
      const w = page.windows[i]; item.countdown.window = w;
      // Used up is a definite state with a known end, so name it instead of showing a bare 0% or an expiry.
      const exhausted = quotaWindowState(w, now) === 'exhausted';
      item.block.dataset.exhausted = String(exhausted); text(item.remainingLabel, exhausted ? '' : '剩余');
      text(item.title, w.label); const value = exhausted ? '已用尽' : quotaRemaining(w); if (item.remaining.data !== value) item.remaining.data = value;
      const scale = `scaleX(${Math.max(0, Math.min(100, w.remainingPercent ?? 0)) / 100})`; if (item.fill.style.transform !== scale) item.fill.style.transform = scale;
      item.fill.dataset.low = String(w.remainingPercent !== null && w.remainingPercent < 15); item.track.dataset.unknown = String(w.remainingPercent === null);
      item.track.hidden = w.remainingPercent === null && (typeof w.remainingValue === 'number' && Number.isFinite(w.remainingValue) || !!w.displayText);
      text(item.meta, w.usedValue !== null && w.limitValue !== null ? `${w.usedValue} / ${w.limitValue} ${w.quotaUnit}` : durationLabel(w.durationSeconds) || '周期未知');
      item.block.title = `${w.label}\n${w.mode} · ${w.accuracy}\n重置 ${absolute(w.resetAt)}\n快照 ${absolute(w.observedAt)}\n${this.settings.timezone}`;
      if (exhausted) item.block.title += w.resetAt ? '\n额度已用尽：重置前保持用尽，不因快照时间较早而判为过期。' : '\n额度已用尽：重置时间未知，按一个周期内保持用尽显示。';
      if (w.accuracy === 'unknown') item.block.title += '\n来源尚未确认此窗口的重置时间，倒计时仅供参考。刷新后以最新快照为准。';
    });
    if (view.empty) text(view.empty, page.ignoredWindows && !page.account.windows.length ? page.ignoredLabel || '额度窗口已忽略' : page.expiredWindows ? '暂无最新额度，等待刷新' : '暂无额度证据');
    setCardInsight(view.node, { kind: 'quota', title: `${page.account.alias} · 请求活动`, data: this.data, accountId: page.account.id, ignoreBaseWeekly: this.settings.ignoreCodexBaseWeekly, ignoreImageQuota: this.settings.ignoreCodexImageQuota, timezone: this.settings.timezone });
  }
}
export class HardwareComponent {
  private history: HardwareSample[][] = [[], [], [], []];
  private devices: string[] = [];
  private observed = 0;
  constructor(private root: HTMLElement, private settings: Settings) { }
  update(data: HardwareData) {
    const at = data.observedAt ? Date.parse(data.observedAt) : 0;
    if (at && this.observed && (at - this.observed > 10000 || at < this.observed)) this.history = [[], [], [], []];
    const devices = [data.cpu, data.gpu, data.vram, data.ram];
    this.root.querySelectorAll<HTMLElement>('.hardware-item').forEach((node, i) => {
      const d = devices[i]; unitValue(node.querySelector('.hardware-value')!, percent(d.load.value));
      node.dataset.level = d.load.value !== null && d.load.value >= 85 ? 'high' : 'normal';
      text(node.querySelector('.hardware-detail')!, i < 2 ? temperature(d.temperature.value) : capacity(d.usedBytes, d.totalBytes));
      text(node.querySelector('.hardware-caption')!, i === 3 ? '物理内存' : i === 2 ? '专用显存' : i === 0 ? (d.name.match(/i[3579]-[\w]+/)?.[0] || d.name) : d.name.replace('AMD Radeon ', '').replace(' Processor', ''));
      node.querySelector<HTMLElement>('.hardware-caption')!.hidden = i >= 2;
      node.title = `${d.name}\n利用率来源：${d.load.source}${i < 2 ? `\n温度来源：${d.temperature.source}` : ''}\n${data.message}`;
      const device = `${d.name}:${d.load.source}:${d.load.sensorId}:${d.temperature.sensorId}`;
      const changed = this.devices[i] !== device;
      if (changed) this.history[i] = [];
      this.devices[i] = device;
      if ((at !== this.observed || changed) && at) { this.history[i].push({ at, value: d.load.value, temperature: i < 2 ? d.temperature.value : null }); if (this.history[i].length > 30) this.history[i].shift(); }
      setCardInsight(node, { kind: 'hardware', title: `${['CPU', 'GPU', '显存', 'RAM'][i]} · ${d.name}`, index: i, name: d.name, samples: this.history[i].slice(), observedAt: at, status: data.status, message: data.message,
        freeBytes: d.usedBytes !== null && d.totalBytes !== null && d.totalBytes > 0 ? Math.max(0, d.totalBytes - d.usedBytes) : null, timezone: this.settings.timezone });
      let svg = node.querySelector<SVGSVGElement>('.sparkline');
      if (!this.settings.trends) svg?.remove();
      if (this.settings.trends && this.history[i].length > 1) {
        if (!svg) {
          svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); svg.classList.add('sparkline');
          // Stretch to the card width; vector-effect keeps the stroke uniform.
          svg.setAttribute('viewBox', '0 0 100 20'); svg.setAttribute('preserveAspectRatio', 'none'); svg.setAttribute('aria-hidden', 'true');
          svg.innerHTML = `<defs><linearGradient id="spark-fill-${i}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="spark-stop-top"/><stop offset="1" class="spark-stop-bottom"/></linearGradient></defs><g class="spark-areas" fill="url(#spark-fill-${i})"></g><g class="spark-lines"></g>`;
          node.append(svg);
        }
        // Null samples split the trend, preserving real gaps.
        let run: [number, number][] = []; const segments: [number, number][][] = [];
        const flush = () => { if (run.length > 1) segments.push(run); run = []; };
        this.history[i].forEach((s, j) => { if (s.value === null) flush(); else run.push([+(j * 100 / (this.history[i].length - 1)).toFixed(2), +(19 - Math.max(0, Math.min(100, s.value)) * .17).toFixed(2)]); }); flush();
        const areas = svg.querySelector('.spark-areas')!, lines = svg.querySelector('.spark-lines')!;
        for (const group of [areas, lines]) while (group.children.length > segments.length) group.lastElementChild!.remove();
        segments.forEach((run, j) => {
          const points = run.map(p => p.join(',')).join(' '), area = `M${run[0][0]} 20L${points.replace(/ /g, 'L')}L${run[run.length - 1][0]} 20Z`;
          let line = lines.children[j], fill = areas.children[j];
          if (!line) { line = document.createElementNS(svg!.namespaceURI, 'polyline'); lines.append(line); }
          if (!fill) { fill = document.createElementNS(svg!.namespaceURI, 'path'); areas.append(fill); }
          if (line.getAttribute('points') !== points) line.setAttribute('points', points);
          if (fill.getAttribute('d') !== area) fill.setAttribute('d', area);
        });
      }
    });
    this.observed = at;
  }
  stale() { this.root.querySelectorAll<HTMLElement>('.hardware-item').forEach(n => { n.dataset.stale = String(!this.observed || Date.now() - this.observed > 10000); }); }
}
