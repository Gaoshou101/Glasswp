import type { QuotaWindow } from './contract';
export const percent = (value: number | null | undefined) => typeof value === 'number' && Number.isFinite(value) ? `${Math.round(Math.max(0, Math.min(100, value)))}%` : '—';
export const temperature = (value: number | null | undefined, suffix = '°C') => typeof value === 'number' && Number.isFinite(value) ? `${Math.round(value)}${suffix}` : '—';
export const capacity = (used: number | null, total: number | null) => used !== null && total !== null && total > 0 ? `${(used / 2 ** 30).toFixed(1)} / ${(total / 2 ** 30).toFixed(0)} GB` : '容量不可用';
export function resetText(window: QuotaWindow, now = Date.now()): string {
  if (!window.resetAt || window.mode === 'non_window') return '重置时间未知';
  const end = Date.parse(window.resetAt);
  if (!Number.isFinite(end)) return '重置时间未知';
  if (end <= now) return '等待更新';
  const seconds = Math.ceil((end - now) / 1000);
  const days = Math.floor(seconds / 86400), hours = Math.floor(seconds % 86400 / 3600);
  const time = days > 0 ? `${days} 天 ${hours} 小时` : [hours, Math.floor(seconds % 3600 / 60), seconds % 60].map(n => String(n).padStart(2, '0')).join(':');
  const accuracy = window.accuracy === 'estimated' ? '约 ' : window.accuracy === 'derived' ? '推算 ' : window.accuracy === 'unknown' ? '参考 ' : '';
  return `${accuracy}${time}${window.mode === 'rolling' ? ' · 滚动恢复' : ' 后重置'}`;
}
export function durationLabel(seconds: number | null): string {
  if (!seconds || seconds <= 0) return '';
  if (seconds === 604800) return '周周期';
  return seconds % 86400 === 0 ? `${seconds / 86400} 天周期` : seconds % 3600 === 0 ? `${seconds / 3600} 小时周期` : `${Math.round(seconds / 60)} 分钟周期`;
}
export function ageLabel(value: string | null, now = Date.now()): string {
  if (!value || !Number.isFinite(Date.parse(value))) return '尚无快照';
  const mins = Math.max(0, Math.floor((now - Date.parse(value)) / 60000));
  return mins === 0 ? '刚刚更新' : mins < 60 ? `${mins} 分钟前` : mins < 1440 ? `${Math.floor(mins / 60)} 小时前` : `${Math.floor(mins / 1440)} 天前`;
}
export function weatherLabel(code: number | null): string {
  if (code === null) return '天气待连接';
  if (code === 0) return '晴'; if (code <= 3) return '多云'; if (code <= 48) return '雾';
  if (code <= 67 || (code >= 80 && code <= 82)) return '雨'; if (code <= 77 || code <= 86 && code >= 85) return '雪';
  return code >= 95 ? '雷雨' : '天气未知';
}
