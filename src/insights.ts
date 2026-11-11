import type { QuotaAccount, QuotaData, QuotaWindow, WeatherData, WeatherHour } from './contract';
import { quotaRemaining } from './format';
import { currentQuotaWindows, quotaWindowState, visibleQuotaWindows } from './quota';

export interface HardwareSample { at: number; value: number | null; temperature: number | null }
export interface SampleStats { count: number; span: number; average: number | null; peak: number | null; low: number | null; temperaturePeak: number | null }
export function sampleStats(samples: HardwareSample[]): SampleStats {
  const values = samples.map(s => s.value).filter((v): v is number => v !== null && Number.isFinite(v));
  const temperatures = samples.map(s => s.temperature).filter((v): v is number => v !== null && Number.isFinite(v));
  return { count: values.length, span: samples.length > 1 ? Math.max(0, samples[samples.length - 1].at - samples[0].at) / 1000 : 0,
    average: values.length ? values.reduce((a, b) => a + b, 0) / values.length : null,
    peak: values.length ? Math.max(...values) : null, low: values.length ? Math.min(...values) : null,
    temperaturePeak: temperatures.length ? Math.max(...temperatures) : null };
}

export function visibleQuotaAccounts(data: QuotaData, ignoreBaseWeekly: boolean, ignoreImageQuota = false, now = Date.now()): QuotaAccount[] {
  return data.accounts.map(account => ({ ...account, windows: visibleQuotaWindows(account, ignoreBaseWeekly, ignoreImageQuota, now) }));
}
const windowKey = (w: QuotaWindow) => JSON.stringify([w.id, w.label, w.durationSeconds, w.mode, w.quotaUnit]);
/** Current evidence, or an exhausted window whose zero balance still holds until its reset. */
export function freshQuota(window: QuotaWindow, now: number): boolean {
  return quotaWindowState(window, now) !== 'expired';
}
const known = (w: QuotaWindow) => w.remainingPercent !== null && Number.isFinite(w.remainingPercent);
const knownValue = (w: QuotaWindow) => known(w) || typeof w.remainingValue === 'number' && Number.isFinite(w.remainingValue) || !!w.displayText;
export function quotaInsight(data: QuotaData, accountId: string, ignoreBaseWeekly: boolean, now = Date.now(), ignoreImageQuota = false) {
  const accounts = visibleQuotaAccounts(data, ignoreBaseWeekly, ignoreImageQuota, now), account = accounts.find(a => a.id === accountId);
  if (!account) return undefined;
  const sourceAvailable = data.status === 'ok' || data.status === 'stale';
  const usable = account.windows.filter(w => sourceAvailable && freshQuota(w, now) && knownValue(w) && (w.mode === 'non_window' || !w.resetAt || !Number.isFinite(Date.parse(w.resetAt)) || Date.parse(w.resetAt) > now));
  const limiting = usable.filter(known).reduce<QuotaWindow | undefined>((current, w) => !current || w.remainingPercent! < current.remainingPercent! ? w : current, undefined);
  const incomplete = usable.length !== account.windows.length;
  let heading = limiting ? `${limiting.label}的相对余量最低` : '需要更新额度证据';
  let explanation = limiting ? '' : '点击上方“刷新”，获取最新用量后再判断。';
  if (!limiting && usable.length) { heading = '已读取当前余额'; explanation = usable.map(w => `${w.label}：${quotaRemaining(w)}`).join('；'); }
  if (limiting?.remainingPercent === 0) { heading = `${limiting.label}已用尽`; explanation = '时间边界到达后需刷新确认恢复；零余额不会自动变成满额。'; }
  if (incomplete && limiting) explanation += ' 部分窗口未知或过期，暂不能判断全部约束。';
  if (!account.windows.length) {
    const original = data.accounts.find(a => a.id === accountId)!;
    heading = currentQuotaWindows(original, now).length ? '当前窗口已被显示设置过滤' : original.windows.length ? '暂无最新额度，等待刷新' : '暂无额度证据';
    explanation = '可在本机设置检查关注账号，或点击上方“刷新”。';
  }
  // Match provider, known plan, and every exact scope/duration. Never combine balances.
  const peers = account.plan && account.windows.length ? accounts.filter(a => a.id !== accountId && a.provider === account.provider && a.plan === account.plan && account.windows.every(w => a.windows.some(other => windowKey(other) === windowKey(w)))) : [];
  const rows = peers.length ? [account, ...peers].map(a => ({ alias: a.alias, selected: a.id === accountId,
    values: account.windows.map(w => {
      const other = a.windows.find(v => windowKey(v) === windowKey(w))!;
      const expired = other.mode !== 'non_window' && other.resetAt && Date.parse(other.resetAt) <= now;
      if (!sourceAvailable || !freshQuota(other, now) || !knownValue(other) || expired) return '待更新';
      return quotaWindowState(other, now) === 'exhausted' ? '已用尽' : quotaRemaining(other);
    }) })) : [];
  return { account, heading, explanation, rows, observedAt: account.windows.map(w => w.observedAt).filter((v): v is string => !!v).sort()[0] || null };
}

export interface QuotaInsightModel { kind: 'quota'; title: string; data: QuotaData; accountId: string; ignoreBaseWeekly: boolean; ignoreImageQuota: boolean; timezone: string }
export interface HardwareInsightModel { kind: 'hardware'; title: string; index: number; name: string; samples: HardwareSample[]; observedAt: number; status: string; message: string; freeBytes: number | null; timezone: string }
export interface WeatherInsightModel { kind: 'weather'; title: string; data: WeatherData; timezone: string }
export interface ClockInsightModel { kind: 'clock'; title: string; timezone: string; worldZones: string[]; seconds: boolean; hour12: boolean; offsetSeconds: number }
export type InsightModel = QuotaInsightModel | HardwareInsightModel | WeatherInsightModel | ClockInsightModel;
export function forecastHours(hours: WeatherHour[] | null | undefined, now = Date.now()): WeatherHour[] {
  const unique = new Map<number, WeatherHour>();
  for (const hour of hours || []) {
    const time = Date.parse(hour.time);
    if (Number.isFinite(time) && time + 3600000 > now && time <= now + 86400000) unique.set(time, hour);
  }
  return [...unique.entries()].sort(([a], [b]) => a - b).slice(0, 12).map(([, hour]) => hour);
}
export const cardInsights = new WeakMap<HTMLElement, InsightModel>();
export function setCardInsight(node: HTMLElement, model: InsightModel) {
  cardInsights.set(node, model); node.dataset.detailTitle = model.title;
  node.tabIndex = 0; node.setAttribute('role', 'button'); node.setAttribute('aria-haspopup', 'dialog');
  if (!node.hasAttribute('aria-expanded')) node.setAttribute('aria-expanded', 'false');
}
