"""Headless checks against a running bridge; never prints pairing or admin tokens."""
import copy
import json
import os
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools' / 'python'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'artifacts' / 'validation'
OUT.mkdir(parents=True, exist_ok=True)
BASE = 'http://127.0.0.1:17632'
CHROME = os.environ.get('WALLPAPER_TEST_CHROME', r'C:\Program Files\Google\Chrome\Application\chrome.exe')
results = {}
errors = []

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=CHROME)
    context = browser.new_context(viewport={'width': 1280, 'height': 900})
    page = context.new_page()
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    TOKEN = page.evaluate("async () => (await api('config/read')).readToken")
    page.goto(BASE + '/?studio', wait_until='networkidle')
    page.locator('#read-token').fill(TOKEN)
    page.locator('#save-connection').click()
    page.wait_for_function("document.getElementById('connection-status').textContent.includes('已连接')")
    page.wait_for_function("document.querySelector('.hardware').textContent.includes('RX 6900 XT')",timeout=60000)
    results['live_browser_connected'] = True
    results['live_values'] = page.locator('.hardware').inner_text()
    assert 'RX 6900 XT' in results['live_values']
    assert '16 GB' in results['live_values']
    assert '苏州' in page.locator('.weather').inner_text()
    page.locator('#glass-target').select_option('quota')
    page.locator('#glass-transparency').fill('100')
    page.locator('#glass-transparency').dispatch_event('input')
    page.locator('#glass-transparency').dispatch_event('change')
    assert page.locator('.quota').evaluate("n => n.style.getPropertyValue('--glass-alpha')") == '0'
    assert page.locator('.quota').evaluate("n => getComputedStyle(n).opacity") == '1'
    page.reload(wait_until='networkidle')
    assert page.locator('.quota').evaluate("n => n.style.getPropertyValue('--glass-alpha')") == '0'
    results['material_persistence_and_text_opacity'] = True
    page.locator('#glass-target').select_option('quota')
    page.locator('#reset-glass').click()
    page.locator('#quota-layout').select_option('carousel')

    snapshot = context.request.get(BASE + '/v1/snapshot', headers={'X-Wallpaper-Token': TOKEN}).json()
    now_ms = int(time.time() * 1000)
    iso = lambda value: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(value / 1000))
    quota = {
        'status': 'ok', 'fetchedAt': iso(now_ms), 'message': '测试已保存快照', 'accounts': [{
            'id': 'test-account', 'alias': 'Codex 测试', 'provider': 'codex', 'plan': 'Pro', 'windows': [
                {'id': 'short', 'label': '5 小时窗口', 'remainingPercent': 0, 'usedValue': None, 'limitValue': None, 'quotaUnit': '', 'resetAt': iso(now_ms + 3000), 'durationSeconds': 18000, 'mode': 'fixed', 'accuracy': 'exact', 'observedAt': iso(now_ms), 'stale': False},
                {'id': 'week', 'label': '周窗口', 'remainingPercent': None, 'usedValue': None, 'limitValue': None, 'quotaUnit': '', 'resetAt': iso(now_ms + 86400000), 'durationSeconds': 604800, 'mode': 'rolling', 'accuracy': 'estimated', 'observedAt': iso(now_ms - 3600000), 'stale': True}]}]}
    fixture = copy.deepcopy(snapshot)
    fixture.pop('revisions', None)
    fixture['quota'] = quota
    def route_snapshot(route): route.fulfill(json=fixture)
    page.route('**/v1/snapshot', route_snapshot)
    page.locator('#save-connection').click()
    page.wait_for_function("document.querySelector('.remaining')?.textContent === '剩余0%'")
    assert page.locator('.remaining').nth(0).inner_text() == '剩余0%'
    assert page.locator('.remaining').nth(1).inner_text() == '剩余—'
    assert '滚动恢复' in page.locator('.reset').nth(1).inner_text()
    page.wait_for_function("document.querySelector('.reset').textContent.includes('等待更新')", timeout=10000)
    assert '数据已过期' in page.locator('.card-freshness:visible').inner_text()
    results['quota_zero_unknown_rolling_stale'] = True

    # Cards show three different accounts together; extra accounts remain reachable.
    quota['accounts'][0]['alias'] = '这是一个用于验证省略与提示的很长账号别名'
    second = copy.deepcopy(quota['accounts'][0])
    second.update(id='second', alias='Team 账号', plan='Team')
    second['windows'] = [copy.deepcopy(second['windows'][1])]
    second['windows'][0]['remainingPercent'] = 97
    third = dict(id='third', alias='Plus 账号', provider='codex', plan='Plus', windows=[])
    quota['accounts'] += [second, third]
    page.locator('#quota-layout').select_option('cards')
    page.locator('#save-connection').click()
    page.wait_for_function("document.querySelectorAll('.quota-card').length === 3")
    assert page.locator('.quota-card .account-alias').count() == 3
    assert page.locator('.quota-card .remaining').nth(0).inner_text() == '剩余0%'
    assert page.locator('.quota-card .remaining').nth(1).inner_text() == '剩余—'
    assert '暂无额度证据' in page.locator('.quota-card').nth(2).inner_text()
    assert not page.locator('.hardware').inner_text().find('内存温度') >= 0
    assert page.locator('.quota-card').nth(0).get_attribute('title') == quota['accounts'][0]['alias']
    page.reload(wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert page.locator('#quota-layout').input_value() == 'cards'
    fourth = copy.deepcopy(second)
    fourth.update(id='fourth', alias='第 4 个账号')
    quota['accounts'].append(fourth)
    page.locator('#save-connection').click()
    page.wait_for_function("document.querySelector('.quota-nav')?.textContent.includes('1 / 2')")
    page.get_by_role('button', name='下一页额度').click()
    assert page.locator('.quota-card').count() == 1
    assert page.locator('.account-alias').inner_text() == '第 4 个账号'
    page.get_by_role('button', name='上一页额度').click()
    assert page.locator('.quota-card').count() == 3
    results['cards_unknown_long_alias_persistence_pagination'] = True

    def audit_layout():
        return page.evaluate("""() => {
          const box = n => { const r = n.getBoundingClientRect(); return {left:r.left,top:r.top,right:r.right,bottom:r.bottom,width:r.width,height:r.height}; };
          const canvas = box(document.querySelector('.wallpaper'));
          const top = box(document.querySelector('.top')), quota = box(document.querySelector('.quota')), hardware = box(document.querySelector('.hardware'));
          const clipped = Array.from(document.querySelectorAll('.time,.date,.temperature,.remaining,.reset,.hardware-value,.hardware-detail')).filter(n => n.scrollWidth > n.clientWidth + 2 || (getComputedStyle(n).overflowY !== 'visible' && n.scrollHeight > n.clientHeight + 2)).map(n => n.className);
          for (const card of document.querySelectorAll('.quota-card')) if (Array.from(card.children).some(n => box(n).bottom > box(card).bottom - 3)) clipped.push('quota-card');
          const nav = document.querySelector('.quota-nav');
          if (nav && box(nav).right > quota.right - 8) clipped.push('quota-nav');
          const overlap = top.bottom >= quota.top || quota.bottom >= hardware.top;
          const outside = [top,quota,hardware].some(r => r.left < canvas.left - 2 || r.right > canvas.right + 2 || r.top < canvas.top || r.bottom > canvas.bottom + 2);
          return {canvas,top,quota,hardware,clipped,overlap,outside};
        }""")
    checks = []
    for width, height in [(720,1280),(800,1280),(900,1440),(1024,1600),(1080,1920),(1200,1920),(1440,2560),(1600,2560),(2160,3840),(1200,1600),(1080,2400),(1440,3440)]:
        page.set_viewport_size({'width':width, 'height':height})
        page.goto(BASE + '/?wallpaper', wait_until='networkidle')
        page.wait_for_selector('.quota-window')
        audit = audit_layout(); audit['size'] = [width,height]
        assert not audit['clipped'] and not audit['overlap'] and not audit['outside'], audit
        checks.append(audit)
    results['responsive_checks'] = checks
    scaling = []
    for dpr in [1.25, 1.6, 2]:
        scaled_context = browser.new_context(viewport={'width':round(1600/dpr),'height':round(2560/dpr)}, device_scale_factor=dpr)
        scaled = scaled_context.new_page()
        scaled.goto(BASE + '/?wallpaper',wait_until='networkidle')
        scaled.evaluate("token=>window.wallpaperPropertyListener.applyUserProperties({bridge_token:{value:token}})",TOKEN)
        scaled.wait_for_function("document.getElementById('connection-status').textContent.includes('已连接')")
        bounds = scaled.locator('.quota').bounding_box()
        scaling.append({'dpr':dpr,'physical_bounds':{k:v*dpr for k,v in bounds.items()}})
        scaled_context.close()
    assert max(s['physical_bounds']['y'] for s in scaling) - min(s['physical_bounds']['y'] for s in scaling) < 2
    results['display_scaling'] = scaling
    page.set_viewport_size({'width':1600,'height':2560})
    page.goto(BASE + '/?wallpaper', wait_until='networkidle')
    page.wait_for_selector('.quota-window')
    page.screenshot(path=str(OUT / 'fixture-1600x2560.png'))

    # Authentication and CORS boundaries (no credentials in the report).
    results['missing_read_ticket_http'] = context.request.get(BASE + '/v1/snapshot').status
    results['remote_origin_http'] = context.request.get(BASE + '/v1/snapshot', headers={'Origin':'https://example.com','X-Wallpaper-Token':TOKEN}).status
    results['admin_wrong_origin_http'] = context.request.post(BASE + '/v1/setup/session', headers={'Origin':'null','X-Wallpaper-Setup':'1'}, data={}).status
    assert results['missing_read_ticket_http'] == 401
    assert results['remote_origin_http'] == 403
    assert results['admin_wrong_origin_http'] == 403
    preflight = context.request.fetch(BASE + '/v1/snapshot', method='OPTIONS', headers={'Origin':'null','Access-Control-Request-Headers':'x-wallpaper-token','Access-Control-Request-Method':'GET'})
    assert preflight.status == 204 and preflight.headers['access-control-allow-origin'] == 'null'
    results['local_file_preflight_http'] = preflight.status

    page.unroute('**/v1/snapshot')
    page.goto((ROOT / 'dist/index.html').as_uri(), wait_until='networkidle')
    page.evaluate("([base,token])=>window.wallpaperPropertyListener.applyUserProperties({bridge_url:{value:base},bridge_token:{value:token}})", [BASE,TOKEN])
    page.wait_for_function("document.getElementById('connection-status').textContent.includes('已连接')",timeout=20000)
    results['file_loopback_connected'] = True
    page.screenshot(path=str(OUT / 'live-1600x2560.png'))
    page.evaluate("window.wallpaperPropertyListener.setPaused(true)")
    assert page.locator('.wallpaper').get_attribute('data-paused') == 'true'
    page.evaluate("window.wallpaperPropertyListener.setPaused(false)")
    results['pause_resume_callback'] = True

    # Disconnect after a successful sample, then recover. Retain last evidence.
    page.route('**/v1/snapshot', lambda r:r.abort())
    page.evaluate("window.wallpaperPropertyListener.setPaused(false)")
    page.wait_for_function("document.querySelector('.note').textContent.includes('断开')",timeout=20000)
    assert 'RX 6900 XT' in page.locator('.hardware').inner_text()
    page.unroute('**/v1/snapshot')
    page.evaluate("window.wallpaperPropertyListener.setPaused(false)")
    page.wait_for_function("document.getElementById('connection-status').textContent.includes('已连接')",timeout=20000)
    results['disconnect_recovery'] = True

    page.set_viewport_size({'width':1200,'height':900})
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.getElementById('status').textContent.includes('已连接')")
    assert '苏州' in page.locator('#current-city').inner_text()
    page.locator('#city-query').fill('Suzhou'); page.locator('#search-city').click()
    page.wait_for_function("document.getElementById('city-results').options.length > 1")
    assert '江苏' in page.locator('#city-results').inner_text()
    page.locator('#load-sensors').click(); page.wait_for_function("document.getElementById('sensor-report').textContent.includes('RX 6900 XT')")
    assert page.locator('#ram-temp').count() == 0
    assert 'D3D Shared Memory' in page.locator('#sensor-report').inner_text()
    results['city_search_sensor_setup'] = True
    assert page.locator('#management-key').input_value() == ''
    results['management_key_not_returned'] = True
    results['page_errors'] = errors
    assert not errors, errors
    browser.close()

(OUT / 'browser-check.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print('Browser checks passed:', ', '.join(key for key in results if key != 'responsive_checks'))
print('Screenshots and report:', OUT)
