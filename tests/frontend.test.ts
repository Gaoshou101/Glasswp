import { availableResets, compactCount } from '../src/account-activity';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ageLabel, percent, resetText } from '../src/format.ts';
import { defaults, loopbackUrl } from '../src/settings.ts';
import type { QuotaWindow } from '../src/contract.ts';
import { isCodexBaseWeekly, isCodexImageQuota, quotaPages } from '../src/quota.ts';
import { forecastHours, quotaInsight, sampleStats } from '../src/insights.ts';
const window: QuotaWindow = { id: 'short', label: '5 小时', remainingPercent: 0, usedValue: null, limitValue: null, quotaUnit: '', resetAt: '2026-10-10T00:00:10Z', durationSeconds: 18000, mode: 'fixed', accuracy: 'exact', observedAt: null, stale: false };
test('zero quota and missing evidence remain distinguishable', () => { assert.equal(percent(0), '0%'); assert.equal(percent(null), '—'); assert.equal(percent(NaN), '—'); assert.equal(percent(120), '100%'); });
test('reset does not invent restored balance at zero', () => { assert.equal(resetText(window, Date.parse('2026-10-10T00:00:09Z')), '00:00:01 后重置'); assert.equal(resetText(window, Date.parse('2026-10-10T00:00:10Z')), '等待更新'); });
test('rolling and estimated boundaries retain their meaning', () => { assert.equal(resetText({ ...window, mode: 'rolling', accuracy: 'estimated' }, Date.parse('2026-10-10T00:00:09Z')), '约 00:00:01 · 滚动恢复'); assert.equal(resetText({ ...window, resetAt: 'invalid' }), '重置时间未知'); });
test('unconfirmed CPAMP boundaries show a reference countdown and preserve unknown or expired states', () => {
  const unconfirmed = { ...window, accuracy: 'unknown' };
  assert.equal(resetText(unconfirmed, Date.parse('2026-10-10T00:00:09Z')), '参考 00:00:01 后重置');
  assert.equal(resetText({ ...unconfirmed, resetAt: null }), '重置时间未知');
  assert.equal(resetText(unconfirmed, Date.parse('2026-10-10T00:00:10Z')), '等待更新');
});
test('bridge URL cannot redirect a display ticket to a remote site', () => { assert.equal(loopbackUrl('http://127.0.0.1:17632'), 'http://127.0.0.1:17632'); for (const u of ['https://example.com', 'http://127.0.0.1.evil.com', 'http://a:b@localhost', 'http://localhost/?key=secret', 'file:///x']) assert.equal(loopbackUrl(u), null); });
test('age is based on observation, not last fetch', () => { assert.equal(ageLabel('2026-10-10T00:00:00Z', Date.parse('2026-10-10T01:01:00Z')), '1 小时前'); assert.equal(ageLabel(null), '尚无快照'); });
test('accepted appearance is default', () => { const s = defaults(); assert.equal(s.resolution, '1600x2560'); assert.deepEqual(s.material.quota, { transparency: 75, blur: 34, edge: 50 }); });

test('cards keep every account and extra window reachable', () => {
  const accounts = Array.from({ length: 4 }, (_, i) => ({ id: String(i), alias: '账号 ' + i, provider: 'codex', plan: '', windows: i === 0 ? [window, { ...window, id: 'week' }, { ...window, id: 'model' }] : i === 2 ? [] : [window] }));
  const pages = quotaPages(accounts, 'cards');
  assert.deepEqual(pages[0].entries.map(e => e.account.id), ['0', '1', '2']);
  assert.equal(pages[0].entries[2].windows.length, 0);
  assert.deepEqual(pages.flatMap(p => p.entries.filter(e => e.account.id === '0').flatMap(e => e.windows.map(w => w.id))), ['short', 'week', 'model']);
  assert.deepEqual(pages[2].entries.map(e => e.account.id), ['3']);
  assert.deepEqual(pages[1].entries.map(e => e.account.id), ['0']);
  assert.ok(pages.every(p => p.entries.length <= 3));
  assert.ok(quotaPages(accounts, 'carousel').every(p => p.entries.length === 1 && p.entries[0].windows.length <= 2));
});

test('uneven quota pages show each account window once in both layouts', () => {
  for (const counts of [[3, 2, 1], [5, 3, 1], [3, 0, 2], [0, 0, 0], [1, 3, 0, 5, 2, 0, 4]]) {
    const accounts = counts.map((count, i) => ({ id: String(i), alias: '账号 ' + i, provider: 'codex', plan: '',
      windows: Array.from({ length: count }, (_, j) => ({ ...window, id: String(j) })) }));
    for (const layout of ['cards', 'carousel'] as const) {
      const pages = quotaPages(accounts, layout);
      for (const account of accounts) {
        const entries = pages.flatMap(page => page.entries).filter(entry => entry.account.id === account.id);
        assert.deepEqual(entries.flatMap(entry => entry.windows.map(w => w.id)), account.windows.map(w => w.id), `${layout}: ${counts}, account ${account.id}`);
        if (!account.windows.length) assert.equal(entries.length, 1, 'empty accounts remain visible once');
      }
      assert.ok(pages.every(page => page.entries.length > 0 && page.entries.length <= (layout === 'cards' ? 3 : 1)));
      assert.ok(pages.flatMap(page => page.entries).every(entry => entry.windows.length <= 2));
    }
  }
  const accounts = [3, 2, 1].map((count, i) => ({ id: String(i), alias: '账号 ' + i, provider: 'codex', plan: '',
    windows: Array.from({ length: count }, (_, j) => ({ ...window, id: String(j), label: j === 2 ? '日窗口 · 绘图（Premium）' : window.label })) }));
  const pages = quotaPages(accounts, 'cards');
  assert.equal(pages.length, 2);
  assert.deepEqual(pages[1].entries.map(entry => [entry.account.id, entry.windows.map(w => w.label)]), [['0', ['日窗口 · 绘图（Premium）']]]);
});

test('optional Codex base weekly filter keeps main quota, other scopes and other providers', () => {
  const main = { ...window, id: 'weekly', label: '周窗口', durationSeconds: 604800 };
  const base = { ...main, id: 'gpt-reserve-weekly-0', label: '周窗口 · 基础模型', stale: true };
  const review = { ...main, id: 'code-review-weekly', label: '周窗口 · 代码审查' };
  const accounts = [{ id: 'codex', alias: 'Codex', provider: 'codex', plan: '', windows: [window, main, base, review] },
    { id: 'claude', alias: 'Claude', provider: 'claude', plan: '', windows: [base] }];
  assert.equal(defaults().ignoreCodexBaseWeekly, false);
  assert.deepEqual(quotaPages(accounts, 'carousel').filter(p => p.entries[0].account.id === 'codex').flatMap(p => p.entries[0].windows.map(w => w.id)), ['short', 'weekly', 'gpt-reserve-weekly-0', 'code-review-weekly']);
  for (const layout of ['cards', 'carousel'] as const) {
    const pages = quotaPages(accounts, layout, true);
    const codex = pages.flatMap(p => p.entries).filter(e => e.account.id === 'codex');
    assert.deepEqual(codex.flatMap(e => e.windows.map(w => w.id)), ['short', 'weekly', 'code-review-weekly']);
    assert.ok(pages.flatMap(p => p.entries).filter(e => e.account.id === 'claude').every(e => e.windows[0].id === base.id));
    assert.ok(codex.every(e => e.account.windows.every(w => w.id !== base.id)));
  }
  assert.equal(accounts[0].windows.length, 4);
  assert.equal(isCodexBaseWeekly({ ...base, durationSeconds: 18000 }), false);
  assert.equal(isCodexBaseWeekly({ ...main, id: 'luna-release-weekly-0' }), true);
  assert.equal(isCodexBaseWeekly({ ...main, label: '周窗口 · base_model_inference' }), true);
  assert.equal(isCodexBaseWeekly({ ...main, id: 'weekly', label: '周窗口 · Spark' }), false);
});

test('ignoring the only base weekly window preserves the account with an explicit ignored state', () => {
  const base = { ...window, id: 'gpt-reserve-weekly-0', label: '周窗口 · 基础模型', durationSeconds: 604800 };
  const accounts = [{ id: 'codex', alias: 'Codex', provider: 'codex', plan: '', windows: [base] }];
  const pages = quotaPages(accounts, 'cards', true);
  assert.equal(pages.length, 1);
  assert.equal(pages[0].entries[0].account.id, 'codex');
  assert.equal(pages[0].entries[0].ignoredWindows, 1);
  assert.equal(pages[0].entries[0].windows.length, 0);
  assert.equal(pages[0].entries[0].account.windows.length, 0);
  assert.equal(quotaPages(accounts, 'cards', false)[0].entries[0].windows[0].id, base.id);
});

test('optional image quota filter is independent of luna-release and keeps real daily quotas', () => {
  const image = { ...window, id: 'imagegen-premium-0-window-1d-0', label: '日窗口 · 绘图（Premium）', durationSeconds: 86400, stale: true };
  const daily = { ...window, id: 'window-1d-0', label: '日窗口', durationSeconds: 86400 };
  const base = { ...window, id: 'gpt-reserve-weekly-0', label: '周窗口 · 基础模型', durationSeconds: 604800 };
  const account = { id: 'codex', alias: 'Codex', provider: 'codex', plan: '', windows: [window, daily, image, base] };
  const other = { ...account, id: 'claude', provider: 'claude', windows: [image] };
  assert.equal(defaults().ignoreCodexImageQuota, false);
  assert.equal(isCodexImageQuota(image), true);
  assert.equal(isCodexImageQuota({ ...image, id: 'cached-header-imagegen-premium-primary', label: '日窗口' }), true);
  assert.equal(isCodexImageQuota({ ...image, id: 'future-window', label: '日窗口 · imagegen_premium' }), true);
  assert.equal(isCodexImageQuota(daily), false);
  assert.equal(isCodexImageQuota(base), false);
  for (const layout of ['cards', 'carousel'] as const) {
    for (const ignoreBase of [false, true]) {
      const pages = quotaPages([account, other], layout, ignoreBase, true);
      const entries = pages.flatMap(page => page.entries);
      assert.deepEqual(entries.filter(e => e.account.id === account.id).flatMap(e => e.windows.map(w => w.id)), ignoreBase ? ['short', 'window-1d-0'] : ['short', 'window-1d-0', 'gpt-reserve-weekly-0']);
      assert.deepEqual(entries.filter(e => e.account.id === other.id).flatMap(e => e.windows.map(w => w.id)), [image.id]);
    }
    assert.deepEqual(quotaPages([account], layout, false, false).flatMap(page => page.entries.flatMap(entry => entry.windows.map(w => w.id))), account.windows.map(w => w.id));
  }
  const hiddenOnly = quotaPages([{ ...account, windows: [image] }], 'cards', false, true)[0].entries[0];
  assert.equal(hiddenOnly.windows.length, 0);
  assert.equal(hiddenOnly.ignoredLabel, '绘图额度已忽略');
  const hiddenBoth = quotaPages([{ ...account, windows: [image, base] }], 'cards', true, true)[0].entries[0];
  assert.equal(hiddenBoth.ignoredLabel, '基础模型周窗口和绘图额度已忽略');
  const insight = quotaInsight({ status: 'ok', fetchedAt: null, message: '', accounts: [{ ...account, windows: [image] }] }, 'codex', false, Date.now(), true)!;
  assert.equal(insight.heading, '当前窗口已被显示设置过滤');
  assert.equal(insight.account.windows.length, 0);
  assert.equal(account.windows.length, 4);
});

test('quota insights compare only identical scopes and known plans, excluding expired evidence', () => {
  const now = Date.parse('2026-10-13T12:00:00Z');
  const fresh = { ...window, observedAt: '2026-10-13T11:59:00Z', resetAt: '2026-10-13T13:00:00Z', remainingPercent: 20 };
  const accounts = [
    { id: 'a', alias: 'A', provider: 'codex', plan: 'Pro', windows: [fresh] },
    { id: 'b', alias: 'B', provider: 'codex', plan: 'Pro', windows: [{ ...fresh, remainingPercent: 80 }] },
    { id: 'plus', alias: 'Plus', provider: 'codex', plan: 'Plus', windows: [fresh] },
    { id: 'scope', alias: 'Spark', provider: 'codex', plan: 'Pro', windows: [{ ...fresh, label: '5 小时 · Spark' }] },
    { id: 'old', alias: 'Old', provider: 'codex', plan: 'Pro', windows: [{ ...fresh, stale: true }] },
    { id: 'expired', alias: 'Expired', provider: 'codex', plan: 'Pro', windows: [{ ...fresh, resetAt: '2026-10-13T11:00:00Z' }] },
  ];
  const data = { status: 'ok' as const, fetchedAt: null, message: '', accounts };
  const insight = quotaInsight(data, 'a', false, now)!;
  assert.deepEqual(insight.rows.map(r => r.alias), ['A', 'B', 'Old', 'Expired']);
  assert.deepEqual(insight.rows.map(r => r.values[0]), ['20%', '80%', '待更新', '待更新']);
  assert.ok(insight.heading.includes('相对余量最低'));
  assert.equal(quotaInsight(data, 'expired', false, now)!.heading, '需要更新额度证据');
  assert.equal(quotaInsight({ ...data, accounts: accounts.map(a => ({ ...a, plan: '' })) }, 'a', false, now)!.rows.length, 0);
});

test('insights preserve zero, unknown and filtered model windows without inferring recovery', () => {
  const now = Date.parse('2026-10-13T12:00:00Z');
  const known = { ...window, observedAt: '2026-10-13T11:59:00Z', resetAt: '2026-10-13T13:00:00Z' };
  const account = { id: 'a', alias: 'A', provider: 'codex', plan: 'Pro', windows: [known, { ...known, id: 'weekly', remainingPercent: null }, { ...known, id: 'luna-release-weekly-0', label: '周窗口 · 基础模型', durationSeconds: 604800 }] };
  const insight = quotaInsight({ status: 'ok', fetchedAt: null, message: '', accounts: [account] }, 'a', true, now)!;
  assert.deepEqual(insight.account.windows.map(w => w.id), ['short', 'weekly']);
  assert.ok(insight.heading.includes('已用尽'));
  assert.ok(insight.explanation.includes('部分窗口未知或过期'));
  assert.ok(insight.explanation.includes('不会自动变成满额'));
  assert.equal(account.windows.length, 3);
});

test('sample summaries count real zero and skip missing values without inventing a full minute', () => {
  const samples = [{ at: 1000, value: 0, temperature: 40 }, { at: 3000, value: null, temperature: null }, { at: 5000, value: 60, temperature: 48 }];
  assert.deepEqual(sampleStats(samples), { count: 2, span: 4, average: 30, peak: 60, low: 0, temperaturePeak: 48 });
  assert.equal(sampleStats([{ at: 1000, value: null, temperature: null }]).peak, null);
});

test('hourly forecast includes the current slot, crosses midnight and drops expired or duplicate slots', () => {
  const now = Date.parse('2026-10-13T23:30:00+08:00');
  const hours = Array.from({ length: 16 }, (_, i) => ({ time: new Date(now - 90 * 60000 + i * 3600000).toISOString(), temperature: i === 1 ? null : 20, precipitationProbability: 0, precipitationMm: 0, code: 2 }));
  const selected = forecastHours([...hours, hours[2], { ...hours[0], time: 'invalid' }], now);
  assert.equal(selected.length, 12);
  assert.equal(selected[0].time, '2026-10-13T15:00:00.000Z');
  assert.equal(selected[1].time, '2026-10-13T16:00:00.000Z');
  assert.equal(selected[0].temperature, null);
  assert.equal(selected[0].precipitationProbability, 0);
  assert.equal(forecastHours(undefined, now).length, 0);
});


test('activity values retain zero and unknown with readable compact counts', () => {
  assert.equal(compactCount(0), '0');
  assert.equal(compactCount(null), '—');
  assert.equal(compactCount(Number.NaN), '—');
  assert.equal(compactCount(-1), '—');
  assert.equal(compactCount(191619993), '191.6M');
});
test('reset badge requires fresh count evidence and never infers automatic resets', () => {
  const now = Date.parse('2026-10-14T12:00:00Z');
  const credits = { available: 0, observedAt: '2026-10-14T11:59:00Z', stale: false };
  assert.equal(availableResets(credits, now), 0);
  assert.equal(availableResets({ ...credits, available: 3 }, now), 3);
  assert.equal(availableResets({ ...credits, stale: true }, now), null);
  assert.equal(availableResets({ ...credits, observedAt: '2026-10-14T11:00:00Z' }, now), null);
  assert.equal(availableResets({ ...credits, available: null }, now), null);
  assert.equal(availableResets(undefined, now), null);
});
