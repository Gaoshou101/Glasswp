import type { QuotaAccount, QuotaWindow } from './contract';
export interface QuotaEntry { account: QuotaAccount; windows: QuotaWindow[]; ignoredWindows?: number; ignoredLabel?: string; expiredWindows?: number }
export interface QuotaPage { key: string; entries: QuotaEntry[] }

// Keep five current windows together, including Antigravity's Argon budget.
export const windowsPerQuotaPage = 5;
export function isCodexBaseWeekly(window: QuotaWindow): boolean {
  const weekly = window.durationSeconds === 604800 || window.durationSeconds === null && /(?:^|-)weekly(?:-\d+)?$/.test(window.id);
  return weekly && (/(?:^| · )(?:基础模型|base_model_inference|luna-release)(?:$| · )/i.test(window.label)
    || /^(?:gpt-reserve|luna-release)-weekly(?:-\d+)?$/.test(window.id));
}
export function isCodexImageQuota(window: QuotaWindow): boolean {
  return /(?:^| · )(?:绘图(?:（[^）]*）)?|imagegen(?:_[a-z0-9_]+)?)(?:$| · )/i.test(window.label)
    || /^(?:cached-header-)?imagegen(?:[-_]|$)/i.test(window.id);
}
export function currentQuotaWindows(account: QuotaAccount, now = Date.now()): QuotaWindow[] {
  return account.windows.filter(w => {
    const observed = Date.parse(w.observedAt || '');
    return !w.stale && Number.isFinite(observed) && observed <= now + 60000 && now - observed <= 900000;
  });
}
export function visibleQuotaWindows(account: QuotaAccount, ignoreCodexBaseWeekly = false, ignoreCodexImageQuota = false, now = Date.now()): QuotaWindow[] {
  return currentQuotaWindows(account, now).filter(w => account.provider !== 'codex' || !(ignoreCodexBaseWeekly && isCodexBaseWeekly(w)) && !(ignoreCodexImageQuota && isCodexImageQuota(w)));
}
export function quotaPages(accounts: QuotaAccount[], layout: 'carousel' | 'cards', ignoreCodexBaseWeekly = false, ignoreCodexImageQuota = false, now = Date.now()): QuotaPage[] {
  const entries: QuotaEntry[] = [], size = layout === 'cards' ? 3 : 1;
  const visible = accounts.map(account => {
    const current = currentQuotaWindows(account, now);
    const windows = visibleQuotaWindows(account, ignoreCodexBaseWeekly, ignoreCodexImageQuota, now);
    const baseHidden = account.provider === 'codex' && ignoreCodexBaseWeekly && current.some(isCodexBaseWeekly);
    const imageHidden = account.provider === 'codex' && ignoreCodexImageQuota && current.some(isCodexImageQuota);
    const ignoredLabel = baseHidden && imageHidden ? '基础模型周窗口和绘图额度已忽略' : imageHidden ? '绘图额度已忽略' : '基础模型周窗口已忽略';
    return { account: { ...account, windows }, ignoredWindows: current.length - windows.length, ignoredLabel, expiredWindows: account.windows.length - current.length };
  });
  for (let i = 0; i < visible.length; i += size) {
    const group = visible.slice(i, i + size);
    const slices = Math.max(1, ...group.map(e => Math.ceil(e.account.windows.length / windowsPerQuotaPage)));
    for (let slice = 0; slice < slices; slice++) {
      entries.push(...group.filter(({ account }) => slice === 0 || slice * windowsPerQuotaPage < account.windows.length)
        .map(entry => ({ ...entry, windows: entry.account.windows.slice(slice * windowsPerQuotaPage, (slice + 1) * windowsPerQuotaPage) })));
    }
  }
  // Pack all entries together so continuation cards cannot leave holes before a later page.
  return Array.from({ length: Math.ceil(entries.length / size) }, (_, i) => {
    const page = entries.slice(i * size, (i + 1) * size);
    return { key: JSON.stringify(page.map(e => [e.account.id, e.windows.map(w => w.id)])), entries: page };
  });
}
