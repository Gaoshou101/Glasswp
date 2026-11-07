"""Check optional luna-release filtering against the running production frontend."""
import copy
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright, expect

BASE = 'http://127.0.0.1:17632'
errors = []
results = {}
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    page = browser.new_page(viewport={'width': 1200, 'height': 1000})
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    cfg = page.evaluate("async () => await api('config/read')")
    snapshot = page.request.get(BASE + '/v1/snapshot', headers={'X-Wallpaper-Token': cfg['readToken']}).json()
    now = time.time()
    iso = lambda value: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(value))
    short = dict(id='five-hour', label='5 小时窗口', remainingPercent=80, usedValue=None, limitValue=None,
                 quotaUnit='', resetAt=iso(now+3600), durationSeconds=18000, mode='fixed', accuracy='exact', observedAt=iso(now), stale=False)
    week = dict(short, id='weekly', label='周窗口', durationSeconds=604800)
    base = dict(week, id='gpt-reserve-weekly-0', label='周窗口 · 基础模型')
    fixture = copy.deepcopy(snapshot)
    fixture.pop('revisions', None)
    fixture['quota'] = dict(status='stale', fetchedAt=iso(now), message='过滤验证数据', accounts=[
        dict(id='first', alias='测试账号 A', provider='codex', plan='Plus', windows=[short, week, base]),
        dict(id='second', alias='测试账号 B', provider='codex', plan='Team', windows=[week]),
        dict(id='third', alias='测试账号 C', provider='codex', plan='Plus', windows=[short, week]),
        dict(id='base-only', alias='测试账号 D', provider='codex', plan='Plus', windows=[base])])
    page.route('**/v1/snapshot', lambda route: route.fulfill(json=fixture))
    # This browser has no physical desktop gesture when simulating WE properties.
    page.route('**/v1/desktop-input', lambda route: route.fulfill(json={'available': True, 'allowed': True}))
    page.goto(BASE + '/?studio', wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    page.locator('#rotate-seconds').fill('0')
    page.locator('#rotate-seconds').dispatch_event('change')
    assert not page.locator('#ignore-codex-base-weekly').is_checked()
    assert page.locator('.quota-nav span').inner_text() == '1 / 2'
    expect(page.locator('.quota-card').first.locator('.window-title').last).to_contain_text('基础模型')
    assert page.locator('.card-freshness:visible').count() == 0
    results['default_preserves_base_weekly'] = True

    page.locator('#ignore-codex-base-weekly').check()
    assert page.locator('.quota-nav span').inner_text() == '1 / 2'
    assert '基础模型' not in ' '.join(page.locator('.window-title').all_inner_texts())
    assert '数据已过期' not in page.locator('.card-freshness').first.inner_text()
    assert page.locator('.quota-card').first.locator('.window-title').all_inner_texts() == ['5 小时窗口', '周窗口']
    results['filtered_before_paging_and_freshness'] = True
    page.get_by_role('button', name='下一页额度').click()
    assert '基础模型周窗口已忽略' in page.locator('.quota-card').inner_text()
    assert '暂无额度证据' not in page.locator('.quota-card').inner_text()
    results['base_only_account_has_explicit_ignored_state'] = True

    page.reload(wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert page.locator('#ignore-codex-base-weekly').is_checked()
    results['browser_setting_persists'] = True
    page.locator('#quota-layout').select_option('carousel')
    for _ in range(4):
        assert '基础模型' not in ' '.join(page.locator('.window-title').all_inner_texts())
        page.get_by_role('button', name='下一页额度').click()
    results['carousel_filter_applies_to_all_accounts'] = True

    page.evaluate("window.wallpaperPropertyListener.applyUserProperties({quota_layout:{value:'cards'},ignore_codex_base_weekly:{value:false}})")
    assert not page.locator('#ignore-codex-base-weekly').is_checked()
    assert page.locator('.quota-nav span').inner_text() == '1 / 2'
    expect(page.locator('.quota-card').first.locator('.window-title').last).to_contain_text('基础模型')
    page.evaluate("window.wallpaperPropertyListener.applyUserProperties({ignore_codex_base_weekly:{value:true}})")
    assert page.locator('#ignore-codex-base-weekly').is_checked()
    assert page.locator('.quota-nav span').inner_text() == '1 / 2'
    results['wallpaper_engine_property_toggle_restores_and_hides'] = True
    assert not errors, errors
    results['browser_errors'] = 0
    browser.close()

out = ROOT / 'artifacts/validation/luna-ignore-result.json'
out.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(results, ensure_ascii=False, indent=2))
