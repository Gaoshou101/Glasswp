"""Serve dist/ on 127.0.0.1:17635; all browser APIs use anonymous fixtures."""
import copy
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17635'
OUT = ROOT / 'artifacts/validation'
OUT.mkdir(parents=True, exist_ok=True)
report = {'enabled': True, 'nextRefreshAt': None, 'state': {'status': 'idle', 'message': '尚未刷新', 'accounts': []}}
accounts = [dict(id='a', alias='工作账号', provider='codex'), dict(id='b', alias='个人账号', provider='codex'), dict(id='c', alias='备用账号', provider='antigravity')]
cfg = dict(cpampUrl='https://example.test', managementConfigured=True, readToken='fixture-only', bridgeUrl=BASE,
           city={'name': '苏州 · 江苏'}, sensors={}, diagnostics={}, quotaRefreshEnabled=True, quotaRefreshMinutes=10,
           quotaRefresh=report, accounts=copy.deepcopy(accounts))
snapshot = {'quota': {'accounts': [dict(id='b', plan='Plus'), dict(id='a', plan='Team'), dict(id='c', plan='')]}}
state = dict(snapshot_failed=False, save_failed=False)
saved, calls, errors, results = [], [], [], {}

def respond(route):
    path = route.request.url.removeprefix(BASE)
    calls.append(path)
    if path == '/v1/setup/session':
        route.fulfill(json={'session': 'fixture-session'})
    elif path == '/v1/setup/config/read':
        route.fulfill(json=cfg)
    elif path == '/v1/setup/quota-refresh/status':
        route.fulfill(json=report)
    elif path == '/v1/snapshot':
        assert route.request.headers['x-wallpaper-token'] == 'fixture-only'
        route.fulfill(status=503 if state['snapshot_failed'] else 200, json={} if state['snapshot_failed'] else snapshot)
    elif path == '/v1/setup/config':
        body = route.request.post_data_json
        if state['save_failed']:
            route.fulfill(status=500, json={'message': '模拟保存失败'})
            return
        saved.append(body)
        if 'accounts' in body:
            by_id = {a['id']: a for a in cfg['accounts']}
            cfg['accounts'] = [dict(by_id[a['id']], alias=a['alias']) for a in body['accounts']]
        route.fulfill(json={'message': '已保存'})
    else:
        raise AssertionError(f'Unexpected API request: {path}')

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport={'width': 1080, 'height': 1000})
    page = context.new_page()
    page.on('pageerror', lambda error: errors.append(str(error)))
    page.route('**/v1/**', respond)
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('.order-plan')?.textContent === 'Team'")
    order = lambda: page.locator('.order-item').evaluate_all('(rows) => rows.map(r => r.dataset.orderId)')
    row = lambda ident: page.locator(f'[data-order-id="{ident}"]')
    assert page.locator('.order-plan').all_text_contents() == ['Team', 'Plus', 'antigravity']
    assert page.locator('#account-order-count').inner_text() == '3 个账号'
    assert row('a').get_by_role('button', name='上移 工作账号').is_disabled()
    assert row('c').get_by_role('button', name='下移 备用账号').is_disabled()
    assert '/v1/setup/quota-refresh' not in calls
    results['plans_join_by_id_and_unknown_state'] = True

    snapshot['quota']['accounts'][2]['plan'] = 'Google AI Pro'
    page.evaluate('readAccountPlans()')
    assert row('c').locator('.order-plan').inner_text() == 'Google AI Pro'
    assert row('c').locator('.order-plan').get_attribute('data-state') == 'known'
    snapshot['quota']['accounts'][2]['plan'] = ''
    page.evaluate('readAccountPlans()')
    assert row('c').locator('.order-plan').inner_text() == 'antigravity'
    results['antigravity_real_subscription_overrides_provider_fallback'] = True

    row('b').get_by_role('button', name='上移 个人账号').click()
    assert order() == ['b', 'a', 'c']
    assert row('b').locator('.order-plan').inner_text() == 'Plus'
    page.evaluate('readConfig()')
    assert order() == ['b', 'a', 'c']
    row('b').get_by_role('button', name='下移 个人账号').focus()
    snapshot['quota']['accounts'][0]['plan'] = 'Pro'
    page.evaluate('readAccountPlans()')
    page.wait_for_function("document.querySelector('[data-order-id=b] .order-plan').textContent === 'Pro'")
    assert row('b').get_by_role('button', name='下移 个人账号').evaluate('(n) => n === document.activeElement')
    assert page.locator('#account-order-status').inner_text() == '顺序未保存'
    row('b').get_by_role('button', name='下移 个人账号').click()
    assert page.locator('#save-account-order').is_disabled()
    row('c').get_by_role('button', name='上移 备用账号').click()
    page.locator('#cancel-account-order').click()
    assert order() == ['a', 'b', 'c'] and not saved
    results['draft_focus_reversion_and_cancel_preserved'] = True

    row('b').get_by_role('button', name='上移 个人账号').click()
    state['save_failed'] = True
    page.locator('#save-account-order').click()
    page.wait_for_function("document.querySelector('#status').textContent === '模拟保存失败'")
    assert order() == ['b', 'a', 'c']
    assert page.locator('#account-order-status').inner_text() == '顺序未保存'
    assert page.locator('#save-account-order').is_enabled()
    state['save_failed'] = False
    page.locator('#save-account-order').click()
    page.wait_for_function("document.querySelector('#account-order-status').textContent === '显示顺序已保存'")
    assert saved[-1] == {'accounts': [dict(id='b', alias='个人账号'), dict(id='a', alias='工作账号'), dict(id='c', alias='备用账号')]}
    page.reload(wait_until='networkidle')
    assert order() == ['b', 'a', 'c']
    results['save_failure_retry_and_reopen'] = True

    state['snapshot_failed'] = True
    page.evaluate('readAccountPlans()')
    assert row('b').locator('.order-plan').inner_text() == 'Pro'
    assert '保留上次' in row('b').locator('.order-plan').get_attribute('title')
    page.reload(wait_until='networkidle')
    assert page.locator('.order-plan').all_text_contents() == ['套餐暂不可用', '套餐暂不可用', 'antigravity']
    row('b').get_by_role('button', name='下移 个人账号').click()
    assert page.locator('#save-account-order').is_enabled()
    state['snapshot_failed'] = False
    page.evaluate('readAccountPlans()')
    assert row('b').locator('.order-plan').inner_text() == 'Pro'
    results['snapshot_failure_preserves_data_and_recovers'] = True

    cfg['accounts'][0]['alias'] = '很长的账号别名' * 10
    snapshot['quota']['accounts'][0]['plan'] = '<img src=x onerror=alert(1)> Enterprise ' * 4
    page.reload(wait_until='networkidle')
    assert page.locator('.order-plan img').count() == 0
    for width in (360, 540, 900, 1440):
        page.set_viewport_size({'width': width, 'height': 1000})
        assert page.locator('#account-order').evaluate('''list => {
            const bounds = list.getBoundingClientRect();
            return [...list.querySelectorAll('.order-name,.order-plan,.order-controls')].every(n => {
                const r = n.getBoundingClientRect(); return r.left >= bounds.left - 1 && r.right <= bounds.right + 1;
            });
        }''')
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
    results['plain_text_metadata_and_four_responsive_widths'] = True

    for path in ('/admin/', '/?studio'):
        markup = context.request.get(BASE + path).text()
        assert 'rel="icon"' in markup and 'favicon.svg' in markup
    icon = context.request.get(page.locator('link[rel=icon]').evaluate('(n) => n.href'))
    assert icon.ok and 'image/svg+xml' in icon.headers['content-type'] and '<svg' in icon.text()
    results['admin_and_preview_favicon'] = True

    cfg['accounts'] = copy.deepcopy(accounts)
    snapshot['quota']['accounts'][0]['plan'] = 'Plus'
    page.set_viewport_size({'width': 1080, 'height': 1000})
    page.reload(wait_until='networkidle')
    page.locator('#account-order-heading').locator('..').screenshot(path=str(OUT / 'admin-settings-order.png'))
    page.set_viewport_size({'width': 360, 'height': 900})
    page.locator('#account-order-heading').locator('..').screenshot(path=str(OUT / 'admin-settings-order-mobile.png'))
    cfg['accounts'] = []
    page.reload(wait_until='networkidle')
    assert page.locator('.order-item').count() == 0
    assert page.locator('#account-order-status').inner_text() == '先选择关注账号'
    assert page.locator('#save-account-order').is_disabled() and page.locator('#cancel-account-order').is_disabled()
    assert not errors, errors
    results['empty_state_and_no_browser_errors'] = True
    browser.close()

(OUT / 'admin-settings-result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Admin settings: {len(results)} checks passed; no browser errors; fixtures only.')
