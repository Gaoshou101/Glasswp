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
