import type { QuotaAccount, QuotaWindow } from './contract';
export interface QuotaEntry { account: QuotaAccount; windows: QuotaWindow[]; ignoredWindows?: number; ignoredLabel?: string }
export interface QuotaPage { key: string; entries: QuotaEntry[] }

// Keep every window reachable, including providers with more than two buckets.
export function isCodexBaseWeekly(window: QuotaWindow): boolean {
  const weekly = window.durationSeconds === 604800 || window.durationSeconds === null && /(?:^|-)weekly(?:-\d+)?$/.test(window.id);
  return weekly && (/(?:^| · )(?:基础模型|base_model_inference|luna-release)(?:$| · )/i.test(window.label)
    || /^(?:gpt-reserve|luna-release)-weekly(?:-\d+)?$/.test(window.id));
}
export function isCodexImageQuota(window: QuotaWindow): boolean {
  return /(?:^| · )(?:绘图(?:（[^）]*）)?|imagegen(?:_[a-z0-9_]+)?)(?:$| · )/i.test(window.label)
    || /^(?:cached-header-)?imagegen(?:[-_]|$)/i.test(window.id);
}
export function visibleQuotaWindows(account: QuotaAccount, ignoreCodexBaseWeekly = false, ignoreCodexImageQuota = false): QuotaWindow[] {
  return account.provider === 'codex' ? account.windows.filter(w => !(ignoreCodexBaseWeekly && isCodexBaseWeekly(w)) && !(ignoreCodexImageQuota && isCodexImageQuota(w))) : account.windows;
}
export function quotaPages(accounts: QuotaAccount[], layout: 'carousel' | 'cards', ignoreCodexBaseWeekly = false, ignoreCodexImageQuota = false): QuotaPage[] {
  const pages: QuotaPage[] = [], size = layout === 'cards' ? 3 : 1;
  const visible = accounts.map(account => {
    const windows = visibleQuotaWindows(account, ignoreCodexBaseWeekly, ignoreCodexImageQuota);
    const baseHidden = account.provider === 'codex' && ignoreCodexBaseWeekly && account.windows.some(isCodexBaseWeekly);
    const imageHidden = account.provider === 'codex' && ignoreCodexImageQuota && account.windows.some(isCodexImageQuota);
    const ignoredLabel = baseHidden && imageHidden ? '基础模型周窗口和绘图额度已忽略' : imageHidden ? '绘图额度已忽略' : '基础模型周窗口已忽略';
    return { account: { ...account, windows }, ignoredWindows: account.windows.length - windows.length, ignoredLabel };
  });
  for (let i = 0; i < visible.length; i += size) {
    const group = visible.slice(i, i + size);
    const slices = Math.max(1, ...group.map(e => Math.ceil(e.account.windows.length / 2)));
    for (let slice = 0; slice < slices; slice++) {
      pages.push({ key: group.map(e => e.account.id).join(':') + ':' + slice,
        // Empty accounts appear on the first page; later pages contain only
        // accounts with more windows, never a replay of their first slice.
        entries: group.filter(({ account }) => slice === 0 || slice * 2 < account.windows.length)
          .map(({ account, ignoredWindows, ignoredLabel }) => ({ account, ignoredWindows, ignoredLabel, windows: account.windows.slice(slice * 2, slice * 2 + 2) })) });
    }
  }
  return pages;
}
