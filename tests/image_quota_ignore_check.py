"""Exercise image-quota visibility in Studio and Wallpaper Engine properties."""
import copy
import json
import os
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
errors = []
results = {}
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport={'width': 1200, 'height': 1000})
    if os.environ.get('WALLPAPER_TEST_BUILT_SCRIPT') == '1':
        context.route('**/wallpaper.js', lambda route: route.fulfill(path=str(ROOT / 'dist/wallpaper.js'), content_type='application/javascript'))
        context.route('**/?studio', lambda route: route.fulfill(path=str(ROOT / 'dist/index.html'), content_type='text/html'))
    page = context.new_page()
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
    image = dict(short, id='imagegen-premium-0-window-1d-0', label='日窗口 · 绘图（Premium）', durationSeconds=86400, observedAt=iso(now-86400), stale=True)
    base = dict(week, id='luna-release-weekly-0', label='周窗口 · 基础模型', observedAt=iso(now-86400), stale=True)
    fixture = copy.deepcopy(snapshot)
    fixture.pop('revisions', None)
    fixture['quota'] = dict(status='stale', fetchedAt=iso(now), message='过滤验证数据', accounts=[
        dict(id='first', alias='测试账号 A', provider='codex', plan='Plus', windows=[short, week, image]),
        dict(id='second', alias='测试账号 B', provider='codex', plan='Team', windows=[short, week]),
        dict(id='third', alias='测试账号 C', provider='codex', plan='Plus', windows=[week])])
    context.add_init_script('if(!localStorage.getItem("wallpaper.dashboard.v1"))localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps({
        'version': 1, 'readToken': cfg['readToken'], 'rotateSeconds': 0, 'quotaLayout': 'cards'
    })) + ');')
    context.route('**/v1/snapshot', lambda route: route.fulfill(json=fixture))
    page.goto(BASE + '/?studio', wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert not page.locator('#ignore-codex-image-quota').is_checked()
    assert page.locator('.quota-nav span').inner_text() == '1 / 2'
    page.get_by_role('button', name='下一页额度').click()
    assert page.locator('.quota-card').count() == 1
    assert '绘图' in page.locator('.window-title').inner_text()
    assert page.locator('.card-freshness:visible').count() == 1
    results['old_settings_preserve_image_by_default'] = True

    page.locator('#ignore-codex-image-quota').check()
    assert page.locator('.quota-nav').count() == 0
    assert page.locator('.quota-card').count() == 3
    assert page.locator('.card-freshness:visible').count() == 0
    assert '绘图' not in ' '.join(page.locator('.window-title').all_inner_texts())
    assert not page.locator('#ignore-codex-base-weekly').is_checked()
    results['hidden_before_paging_and_freshness'] = True
    page.reload(wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert page.locator('#ignore-codex-image-quota').is_checked()
    assert page.locator('.quota-nav').count() == 0
    results['studio_setting_survives_reload'] = True

    fixture['quota']['accounts'][0]['windows'].append(base)
    page.reload(wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    page.get_by_role('button', name='下一页额度').click()
    assert page.locator('.window-title').all_inner_texts() == ['周窗口 · 基础模型']
    page.locator('#ignore-codex-base-weekly').check()
    assert page.locator('.quota-nav').count() == 0
    page.locator('#ignore-codex-image-quota').uncheck()
    page.get_by_role('button', name='下一页额度').click()
    assert page.locator('.window-title').all_inner_texts() == ['日窗口 · 绘图（Premium）']
    results['image_and_luna_toggles_independent'] = True

    page.evaluate("window.wallpaperPropertyListener.applyUserProperties({ignore_codex_image_quota:{value:true}})")
    assert page.locator('#ignore-codex-image-quota').is_checked()
    assert page.locator('.quota-nav').count() == 0
    page.evaluate("window.wallpaperPropertyListener.applyUserProperties({ignore_codex_image_quota:{value:false}})")
    assert not page.locator('#ignore-codex-image-quota').is_checked()
    assert page.locator('.quota-nav span').inner_text() == '1 / 2'
    results['we_property_hides_and_restores'] = True

    fixture['quota']['accounts'][0]['windows'] = [image]
    page.evaluate("window.wallpaperPropertyListener.applyUserProperties({ignore_codex_image_quota:{value:true}})")
    page.reload(wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert '绘图额度已忽略' in page.locator('.quota-card').first.inner_text()
    assert '暂无额度证据' not in page.locator('.quota-card').first.inner_text()
    assert page.locator('.card-freshness:visible').count() == 0
    page.locator('#quota-layout').select_option('carousel')
    for _ in range(3):
        assert '绘图' not in ' '.join(page.locator('.window-title').all_inner_texts())
        page.get_by_role('button', name='下一页额度').click()
    results['image_only_account_preserved_and_carousel_filtered'] = True
    assert not errors, errors
    results['browser_errors'] = 0
    browser.close()

out = ROOT / 'artifacts/validation/image-quota-ignore-result.json'
out.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(results, ensure_ascii=False, indent=2))
