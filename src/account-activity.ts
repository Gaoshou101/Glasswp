import type { AccountActivity, ResetCreditData } from './contract';
import { loopbackUrl } from './settings';

export const activityRanges = [['today', '今天'], ['yesterday', '昨天'], ['7d', '最近 7 天'], ['14d', '最近 14 天'], ['30d', '最近 30 天'], ['all', '全部'], ['custom', '自定义']] as const;
export type ActivityRange = typeof activityRanges[number][0];
const localInput = (ms: number) => {
  const date = new Date(ms), pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
};

export const compactCount = (value: number | null | undefined) => {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) return '—';
  const unit = value >= 1e9 ? [1e9, 'B'] as const : value >= 1e6 ? [1e6, 'M'] as const : value >= 1e3 ? [1e3, 'K'] as const : [1, ''] as const;
  return unit[0] === 1 ? Math.round(value).toLocaleString('en-US') : `${(value / unit[0]).toFixed(1).replace(/\.0$/, '')}${unit[1]}`;
};
export const availableResets = (credits: ResetCreditData | null | undefined, now = Date.now()) => credits && !credits.stale &&
  credits.observedAt && now - Date.parse(credits.observedAt) <= 900000 && Date.parse(credits.observedAt) <= now + 60000 &&
  credits.available !== null && Number.isInteger(credits.available) && credits.available >= 0 ? credits.available : null;

/** Uses the existing one-second lifecycle tick, only while this account is open. */
export class AccountActivityControl {
  data?: AccountActivity;
  loading = false;
  message = '';
  range: ActivityRange = '7d';
  rangeError = '';
  customStart = '';
  customEnd = '';
  page = 0;
  readonly timezone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  private customBounds?: { fromMs: number; toMs: number };
  private cursors: { beforeMs: number; beforeId: number }[] = [];
  private bounds?: { fromMs: number; toMs: number };
  private latestActivity: string | null = null;
  private paging = false;
  private key = '';
  private id = '';
  private url = '';
  private token = '';
  private next = 0;
  private active = false;
  private controller?: AbortController;
  constructor(private changed: () => void) {}
  configure(id: string, url: string, token: string, active: boolean) {
    const key = JSON.stringify([id, url, token, this.range, this.customBounds]);
    if (key !== this.key) { this.close(); this.key = key; this.id = id; this.url = loopbackUrl(url) || ''; this.token = token; }
    this.active = active;
    if (!active) { if (this.controller) this.next = 0; this.controller?.abort(); this.controller = undefined; this.loading = false; return; }
    if (!this.loading && Date.now() >= this.next && (this.range !== 'custom' || this.customBounds)) void this.load();
  }
  close() { this.controller?.abort(); this.controller = undefined; this.key = ''; this.active = false; this.data = undefined; this.loading = false; this.message = ''; this.next = 0; this.page = 0; this.paging = false; this.cursors = []; this.bounds = undefined; this.latestActivity = null; }
  selectRange(range: string) {
    if (!activityRanges.some(([value]) => value === range) || this.range === range) return;
    if (range === 'custom' && !this.customStart) {
      const day = new Date(); day.setHours(0, 0, 0, 0);
      this.customStart = localInput(day.getTime()); this.customEnd = localInput(Date.now());
    }
    this.range = range as ActivityRange; this.rangeError = ''; this.key = ''; this.changed();
  }
  applyCustom() {
    const fromMs = Date.parse(this.customStart), toMs = Date.parse(this.customEnd);
    if (!Number.isFinite(fromMs) || !Number.isFinite(toMs) || fromMs <= 0 || fromMs >= toMs) {
      this.rangeError = '开始时间必须早于结束时间'; this.changed(); return;
    }
    this.customBounds = { fromMs, toMs }; this.rangeError = ''; this.key = ''; this.changed();
  }
  retry() { if (this.active && !this.loading && (this.range !== 'custom' || this.customBounds)) { if (this.page > 0 && this.data) this.data = { ...this.data, recent: [], hasMore: false }; this.page = 0; this.paging = false; this.cursors = []; this.next = 0; void this.load(); } }
  turnPage(direction: -1 | 1) {
    if (!this.active || this.loading || !this.data) return;
    if (direction === -1 && this.page === 0) return;
    if (direction === 1) {
      if (!this.data.hasMore || !this.data.nextBeforeMs || !this.data.nextBeforeId) return;
      this.cursors[this.page] = { beforeMs: this.data.nextBeforeMs, beforeId: this.data.nextBeforeId };
    }
    this.page += direction;
    this.paging = true;
    this.data = { ...this.data, recent: [], hasMore: false };
    void this.load();
  }
  private async load() {
    if (!this.key || !this.token || !this.url) { this.message = '请先连接本机数据'; this.next = Date.now() + 60000; return; }
    const controller = new AbortController(), key = this.key, page = this.page;
    this.controller = controller; this.loading = true; this.message = ''; this.next = page > 0 ? Infinity : Date.now() + 60000;
    this.changed();
    const timeout = window.setTimeout(() => controller.abort(), 18000);
    try {
      const query = new URLSearchParams({ range: this.range, timeZone: this.timezone });
      const bounds = this.paging ? this.bounds : this.range === 'custom' ? this.customBounds : undefined;
      if (bounds) { query.set('fromMs', String(bounds.fromMs)); query.set('toMs', String(bounds.toMs)); }
      if (page > 0) { query.set('beforeMs', String(this.cursors[page - 1].beforeMs)); query.set('beforeId', String(this.cursors[page - 1].beforeId)); }
      const response = await fetch(`${this.url}/v1/accounts/${encodeURIComponent(this.id)}/activity?${query}`, { headers: { 'X-Wallpaper-Token': this.token }, cache: 'no-store', signal: controller.signal });
      if (!response.ok) throw new Error('unavailable');
      const data = await response.json() as AccountActivity;
      if (controller.signal.aborted || key !== this.key || this.controller !== controller || page !== this.page) return;
      if (data.accountId !== this.id || !Array.isArray(data.recent)) throw new Error('invalid account activity');
      if (page === 0) { this.bounds = { fromMs: Date.parse(data.from), toMs: Date.parse(data.to) }; this.latestActivity = data.lastActivity; this.cursors = []; this.paging = false; }
      this.data = page === 0 ? data : { ...data, lastActivity: this.latestActivity };
    } catch {
      if (this.controller === controller && key === this.key) this.message = this.data ? '连接暂不可用，显示上次活动' : '请求活动暂不可用，可稍后重试';
    } finally {
      clearTimeout(timeout);
      if (this.controller === controller) { this.controller = undefined; this.loading = false; this.changed(); }
    }
  }
}
