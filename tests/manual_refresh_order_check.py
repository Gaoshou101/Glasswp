"""Verify staged refresh controls and order editing without reading credentials or changing production config."""
import copy
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17633'
LIVE = 'http://127.0.0.1:17632'
OUT = ROOT / 'artifacts/validation'
results, errors = {}, []
iso = lambda value: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(value))

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport={'width': 1600, 'height': 2560})
    page = context.new_page()
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.goto(LIVE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    public_config = page.evaluate("async () => await api('config/read')")
    live_snapshot = context.request.get(LIVE + '/v1/snapshot', headers={'X-Wallpaper-Token': public_config['readToken']}).json()

    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    staged_config = page.evaluate("async () => await api('config/read')")
    token = staged_config['readToken']
    actual = context.request.get(BASE + '/v1/quota-refresh/status', headers={'X-Wallpaper-Token': token}).json()
    assert actual['canRefresh'] is False
    assert 'managementKey' not in json.dumps(actual) and 'authFile' not in json.dumps(actual)
    assert context.request.post(BASE + '/v1/quota-refresh').status == 401
    assert context.request.post(BASE + '/v1/quota-refresh', headers={'Origin': 'https://example.test', 'X-Wallpaper-Token': token}).status == 403
    assert context.request.post(BASE + '/v1/setup/config', headers={'Origin': BASE, 'X-Wallpaper-Token': token}, data={}).status == 403
    assert context.request.post(BASE + '/v1/quota-refresh', headers={'Origin': 'null', 'X-Wallpaper-Token': token}).status == 400
    preflight = context.request.fetch(BASE + '/v1/quota-refresh', method='OPTIONS', headers={'Origin': 'null', 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'x-wallpaper-token'})
    assert preflight.status == 204 and 'POST' in preflight.headers['access-control-allow-methods']
    results['limited_refresh_auth_and_file_origin'] = True

    cfg = copy.deepcopy(staged_config)
    cfg['managementConfigured'] = True
    cfg['accounts'] = copy.deepcopy(public_config['accounts'])
    assert len(cfg['accounts']) == 3
    captured = []
    page.route('**/v1/setup/config/read', lambda route: route.fulfill(json=cfg))
    def save_config(route):
        body = route.request.post_data_json
        captured.append(body)
        if 'accounts' in body:
            by_id = {a['id']: a for a in cfg['accounts']}
            cfg['accounts'] = [dict(by_id[a['id']], alias=a['alias']) for a in body['accounts']]
        route.fulfill(json={'message': '显示顺序已保存'})
    page.route('**/v1/setup/config', save_config)
    page.reload(wait_until='networkidle')
    page.wait_for_selector('.order-item')
    original = [a['id'] for a in cfg['accounts']]
    assert page.locator('.order-item').first.locator('button').first.is_disabled()
    assert page.locator('.order-item').last.locator('button').last.is_disabled()
    page.locator('.order-item').nth(1).locator('button').first.click()
    assert page.locator('#account-order-status').inner_text() == '顺序未保存'
    expected = [original[1], original[0], original[2]]
    assert page.locator('.order-item').evaluate_all('(rows) => rows.map(n => n.dataset.orderId)') == expected
    # Unrelated config reads must not overwrite pending order edits.
    page.evaluate('readConfig()')
    assert page.locator('.order-item').evaluate_all('(rows) => rows.map(n => n.dataset.orderId)') == expected
    page.locator('#save-account-order').click()
    page.wait_for_function("document.querySelector('#account-order-status').textContent.includes('已保存')")
    assert [a['id'] for a in captured[-1]['accounts']] == expected
    assert [a['alias'] for a in captured[-1]['accounts']] == [a['alias'] for a in cfg['accounts']]
    page.reload(wait_until='networkidle')
    assert page.locator('.order-item').evaluate_all('(rows) => rows.map(n => n.dataset.orderId)') == expected
    results['admin_order_draft_save_reopen_and_aliases'] = True
    page.locator('#account-order-heading').locator('..').screenshot(path=str(OUT / 'account-order-settings.png'))

    fixture = copy.deepcopy(live_snapshot)
    by_id = {a['id']: a for a in fixture['quota']['accounts']}
    fixture['quota']['accounts'] = [by_id[a] for a in expected]
    page.route('**/v1/snapshot', lambda route: route.fulfill(json=fixture))
    report = copy.deepcopy(actual)
    report['canRefresh'] = True
    report['snapshotFetchedAt'] = fixture['quota']['fetchedAt']
    report['refresh']['state'] = {'status': 'idle', 'message': '尚未刷新', 'lastFinishedAt': None}
    posted = []
    fail_next = [False]
    def refresh_route(route):
        if route.request.method == 'POST':
            posted.append(time.time())
            if fail_next[0]:
                fail_next[0] = False
                route.fulfill(status=502, json={'message': 'test failure'})
                return
            report['refresh']['state'] = {'status': 'queued', 'message': '已排队', 'lastFinishedAt': None}
        route.fulfill(json=report)
    page.route('**/v1/quota-refresh', refresh_route)
    page.route('**/v1/quota-refresh/status', lambda route: route.fulfill(json=report))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps({
        'version': 1, 'bridgeUrl': BASE, 'readToken': token, 'rotateSeconds': 0, 'quotaLayout': 'cards', 'ignoreCodexBaseWeekly': True
    })) + ');')
    page.goto(BASE + '/?wallpaper', wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    page.wait_for_function("!document.querySelector('#refresh-quota').disabled")
    assert page.locator('.quota-card').evaluate_all('(rows) => rows.map(n => n.dataset.accountId)') == expected
    page.locator('#refresh-quota').click()
    page.wait_for_function("document.querySelector('#refresh-quota').textContent.includes('刷新中')")
    assert page.locator('#refresh-quota').is_disabled() and len(posted) == 1
    page.locator('#refresh-quota').evaluate('(n) => n.click()')
    assert len(posted) == 1
    # The UI must wait for rereading the observation, even after upstream completion.
    finished = iso(time.time())
    report['refresh']['state'] = {'status': 'ok', 'message': '3/3 个账号已更新', 'lastFinishedAt': finished}
    report['snapshotFetchedAt'] = iso(time.time() - 120)
    page.wait_for_timeout(3100)
    assert page.locator('#refresh-quota').is_disabled()
    report['snapshotFetchedAt'] = fixture['quota']['fetchedAt'] = finished
    first_window = fixture['quota']['accounts'][0]['windows'][0]
    first_window['remainingPercent'] = 73
    page.wait_for_function("document.querySelector('#refresh-quota').textContent.includes('已刷新')", timeout=12000)
    page.wait_for_function("document.querySelector('.remaining').textContent.includes('73%')")
    assert not page.locator('#refresh-quota').is_disabled()
    results['refresh_single_post_busy_waits_for_new_snapshot'] = True

    # Failure is actionable and retryable, rather than claiming success.
    fail_next[0] = True
    page.locator('#refresh-quota').click()
    page.wait_for_function("document.querySelector('#refresh-quota').textContent.includes('重试')")
    assert not page.locator('#refresh-quota').is_disabled()
    page.locator('#refresh-quota').click()
    page.wait_for_function("document.querySelector('#refresh-quota').disabled")
    report['refresh']['state'] = {'status': 'partial', 'message': '2/3 个账号已更新；其余保留上次快照', 'lastFinishedAt': finished}
    page.wait_for_function("document.querySelector('#refresh-quota').textContent.includes('部分失败')", timeout=12000)
    assert not page.locator('#refresh-quota').is_disabled()
    results['failure_retry_partial_and_existing_quota_retained'] = True

    # Header controls must fit all supported portrait sizes, including pagination.
    fixture['quota']['accounts'][0]['windows'].append(dict(first_window, id='extra-review', label='周窗口 · 代码审查'))
    fixture['quota']['accounts'][0]['windows'].append(dict(first_window, id='extra-spark', label='周窗口 · Spark'))
    page.reload(wait_until='networkidle')
    page.wait_for_selector('.quota-nav')
    checks = []
    for width, height in [(720,1280),(800,1280),(900,1440),(1024,1600),(1080,1920),(1200,1920),(1440,2560),(1600,2560),(2160,3840),(1200,1600),(1080,2400),(1440,3440)]:
        page.set_viewport_size({'width': width, 'height': height})
        page.wait_for_timeout(80)
        check = page.evaluate("""() => {
          const h=document.querySelector('.quota-heading').getBoundingClientRect(), a=document.querySelector('.quota-actions').getBoundingClientRect(), title=document.querySelector('.quota-heading>span').getBoundingClientRect();
          return {rightOverflow:a.right>h.right+1, titleOverlap:title.right>a.left-4, verticalOverflow:a.bottom>h.bottom+1};
        }""")
        assert not any(check.values()), (width, height, check)
        checks.append([width,height])
    results['header_refresh_and_pagination_12_portrait_sizes'] = checks
    page.set_viewport_size({'width':1600,'height':2560})
    page.locator('.quota').screenshot(path=str(OUT / 'manual-refresh-quota.png'))
    # Unconfigured status disables the control while keeping the saved card order.
    report['canRefresh'] = False
    page.reload(wait_until='networkidle')
    page.wait_for_function("document.querySelector('#refresh-quota').disabled")
    results['unconfigured_refresh_disabled'] = True
    assert not errors, errors
    results['browser_errors'] = 0
    browser.close()

(OUT / 'manual-refresh-order-result.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print('Manual refresh and order checks passed: auth, admin save/reopen, busy/snapshot/partial/retry, 12 portrait sizes; no browser errors.')
