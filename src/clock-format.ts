export const defaultWorldZones = ['Asia/Shanghai', 'Asia/Tokyo', 'Asia/Singapore', 'Europe/London', 'America/New_York', 'America/Los_Angeles'];
export function validTimezone(value: unknown): value is string {
  if (typeof value !== 'string' || !value.trim() || value.length > 128) return false;
  try { new Intl.DateTimeFormat('en', { timeZone: value.trim() }); return true; } catch { return false; }
}
export function normalizeWorldZones(value: unknown): string[] {
  if (!Array.isArray(value)) return [...defaultWorldZones];
  return [...new Set(value.filter(validTimezone).map(zone => zone.trim()))].slice(0, 24);
}
export function clockNow(offsetSeconds: number, now = Date.now()): Date {
  return new Date(now + (Number.isFinite(offsetSeconds) ? Math.max(-86400, Math.min(86400, offsetSeconds)) : 0) * 1000);
}
const zoneNames: Record<string, string> = { 'Asia/Shanghai': '北京', 'Asia/Tokyo': '东京', 'Asia/Singapore': '新加坡', 'Asia/Hong_Kong': '香港', 'Europe/London': '伦敦', 'Europe/Paris': '巴黎', 'America/New_York': '纽约', 'America/Los_Angeles': '洛杉矶', 'Australia/Sydney': '悉尼', 'Asia/Dubai': '迪拜', UTC: '协调世界时' };
export const timezoneName = (timezone: string) => zoneNames[timezone] || timezone.split('/').pop()!.replace(/_/g, ' ');

export class ClockFormats {
  private key = '';
  private time!: Intl.DateTimeFormat;
  private date!: Intl.DateTimeFormat;
  private day!: Intl.DateTimeFormat;
  format(timezone: string, hour12: boolean, seconds: boolean, now = new Date()) {
    const key = `${timezone}:${hour12}:${seconds}`;
    if (this.key !== key) {
      this.time = new Intl.DateTimeFormat('en-GB', { timeZone: timezone, hour: '2-digit', minute: '2-digit', ...(seconds ? { second: '2-digit' } : {}), hour12 });
      this.date = new Intl.DateTimeFormat('zh-CN', { timeZone: timezone, year: 'numeric', month: '2-digit', day: '2-digit' });
      this.day = new Intl.DateTimeFormat('zh-CN', { timeZone: timezone, weekday: 'long' });
      this.key = key;
    }
    return { time: this.time.format(now), date: `${this.date.format(now).replace(/\//g, '.')}　${this.day.format(now)}` };
  }
}

/** Intl resolves actual offsets for each instant, including DST and fractional-hour zones. */
export class WorldClockFormats {
  private cache = new Map<string, { clock: ClockFormats; day: Intl.DateTimeFormat; offset: Intl.DateTimeFormat }>();
  private zone(timezone: string) {
    let item = this.cache.get(timezone);
    if (!item) {
      item = { clock: new ClockFormats(), day: new Intl.DateTimeFormat('en', { timeZone: timezone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }), offset: new Intl.DateTimeFormat('en', { timeZone: timezone, timeZoneName: 'longOffset' }) };
      // Bound formatter retention even after repeated admin edits.
      if (this.cache.size >= 48) this.cache.delete(this.cache.keys().next().value!);
      this.cache.set(timezone, item);
    }
    return item;
  }
  private day(timezone: string, now: Date) {
    const parts = this.zone(timezone).day.formatToParts(now);
    const part = (name: string) => Number(parts.find(p => p.type === name)?.value);
    return Date.UTC(part('year'), part('month') - 1, part('day')) / 86400000;
  }
  format(timezone: string, reference: string, hour12: boolean, seconds: boolean, now = new Date()) {
    const item = this.zone(timezone), formatted = item.clock.format(timezone, hour12, seconds, now);
    const difference = this.day(timezone, now) - this.day(reference, now);
    const offset = item.offset.formatToParts(now).find(p => p.type === 'timeZoneName')?.value.replace('GMT', 'UTC') || 'UTC';
    const parts = item.day.formatToParts(now);
    const minutesOfDay = Number(parts.find(p => p.type === 'hour')!.value) * 60 + Number(parts.find(p => p.type === 'minute')!.value);
    return { timezone, name: timezoneName(timezone), time: formatted.time, date: formatted.date, offset, minutesOfDay, dayDifference: difference, dayLabel: difference === 0 ? '同日' : difference === -1 ? '前一天' : difference === 1 ? '后一天' : `${difference > 0 ? '+' : ''}${difference} 天` };
  }
}
