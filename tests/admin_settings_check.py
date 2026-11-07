"""Settings page regression: serves dist/ on an ephemeral 127.0.0.1 port; every API uses anonymous fixtures."""
import copy
import datetime
import functools
import http.server
import json
import sys
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'artifacts/validation'
OUT.mkdir(parents=True, exist_ok=True)


class StaticHandler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map, '.js': 'text/javascript', '.svg': 'image/svg+xml', '.css': 'text/css', '.html': 'text/html'}
    def log_message(self, *args):
        pass


server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(StaticHandler, directory=str(ROOT / 'dist')))
threading.Thread(target=server.serve_forever, daemon=True).start()
BASE = f'http://127.0.0.1:{server.server_address[1]}'


def local_iso(seconds_ago=0):
    # The bridge reports local times with seven fractional digits and an offset.
    moment = datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))) - datetime.timedelta(seconds=seconds_ago)
    return f'{moment:%Y-%m-%dT%H:%M:%S}.{moment.microsecond:06d}0+08:00'


report = {'enabled': True, 'nextRefreshAt': None, 'state': {'status': 'idle', 'message': '尚未刷新', 'accounts': []}}
accounts = [dict(id='a', alias='工作账号', provider='codex'), dict(id='b', alias='个人账号', provider='codex'), dict(id='c', alias='备用账号', provider='antigravity')]
discovery = [dict(id='a', alias='工作账号', provider='codex', label='codex-a.json'), dict(id='b', alias='个人账号', provider='codex', label='codex-b.json'),
             dict(id='d', alias='codex 3', provider='codex', label='codex-d.json'), dict(id='e', alias='antigravity 4', provider='antigravity', label='ag-e.json')]
sensors = [dict(id='cpu/0/temperature/0', hardware='Fixture CPU', hardwareId='cpu/0', hardwareType='Cpu', name='CPU Package', type='Temperature', value=55.25),
           dict(id='cpu/0/load/0', hardware='Fixture CPU', hardwareId='cpu/0', hardwareType='Cpu', name='CPU Total', type='Load', value=12.5),
           dict(id='gpu/0/temperature/0', hardware='Fixture GPU', hardwareId='gpu/0', hardwareType='GpuNvidia', name='GPU Core', type='Temperature', value=None),
           dict(id='gpu/0/load/0', hardware='Fixture GPU', hardwareId='gpu/0', hardwareType='GpuNvidia', name='GPU Core', type='Load', value=30.0)]
client = dict(at=local_iso(3), buildId='fixture-build', host='wallpaper-engine', width=1600, height=2560, fps=30, paused=False, materials={'top': [83, 26, 68]})
cfg = dict(cpampUrl='https://example.test', managementConfigured=True, readToken='fixture-only', bridgeUrl=BASE,
           city={'id': '1', 'name': '苏州 · 江苏'}, sensors={}, quotaRefreshEnabled=True, quotaRefreshMinutes=10, quotaRefresh=report,
           accounts=copy.deepcopy(accounts), diagnostics=dict(lastClient=client, lastSnapshotRead=local_iso(3), cacheFaults=[], desktopInput=None))
snapshot = {'quota': {'accounts': [dict(id='b', plan='Plus'), dict(id='a', plan='Team'), dict(id='c', plan='')]}}
state = dict(snapshot_failed=False, save_failed=False, confirm=True)
candidates, saved, calls, errors, dialogs, results = set(), [], [], [], [], {}


def respond(route):
    path = route.request.url.removeprefix(BASE)
    calls.append(path)
    if path == '/v1/setup/session':
        route.fulfill(json={'session': 'fixture-session'})
    elif path == '/v1/setup/config/read':
        route.fulfill(json=cfg)
    elif path == '/v1/setup/quota-refresh/status':
        route.fulfill(json=report)
    elif path == '/v1/setup/sensors':
        route.fulfill(json={'sensors': sensors})
    elif path == '/v1/setup/accounts':
        candidates.clear(); candidates.update(a['id'] for a in discovery)
        watched = {a['id'] for a in cfg['accounts']}
        route.fulfill(json={'accounts': [dict(a, alias=next((s['alias'] for s in cfg['accounts'] if s['id'] == a['id']), a['alias']), selected=a['id'] in watched) for a in discovery]})
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
            # Like the bridge: only discovered or already watched accounts may be saved; blank aliases keep the source alias.
            known = {**{a['id']: a for a in discovery if a['id'] in candidates}, **{a['id']: a for a in cfg['accounts']}}
            if any(a['id'] not in known for a in body['accounts']):
                route.fulfill(status=400, json={'message': '请选择已发现的账号'})
                return
            cfg['accounts'] = [dict(id=a['id'], alias=a['alias'].strip() or known[a['id']]['alias'], provider=known[a['id']]['provider']) for a in body['accounts']]
        for key in ('quotaRefreshEnabled', 'quotaRefreshMinutes', 'sensors'):
            if key in body:
                cfg[key] = body[key]
        if body.get('clearManagementKey'):
            cfg['managementConfigured'] = False
        route.fulfill(json={'message': '已保存'})
    else:
        raise AssertionError(f'Unexpected API request: {path}')


def on_dialog(dialog):
    dialogs.append((dialog.type, dialog.message))
    if dialog.type == 'confirm' and not state['confirm']:
        dialog.dismiss()
    else:
        dialog.accept()


with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport={'width': 1080, 'height': 1000})
    page = context.new_page()
    page.on('pageerror', lambda error: errors.append(str(error)))
    page.on('dialog', on_dialog)
    page.route('**/v1/**', respond)
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('.order-plan')?.textContent === 'Team'")
    order = lambda: page.locator('.account-row').evaluate_all('(rows) => rows.map(r => r.dataset.accountId)')
    row = lambda ident: page.locator(f'.account-row[data-account-id="{ident}"]')
    unsaved = lambda: page.locator('#unsaved').inner_text() if page.locator('#unsaved').is_visible() else ''
    unload_blocked = lambda: page.evaluate("() => { const e = new Event('beforeunload', { cancelable: true }); dispatchEvent(e); return e.defaultPrevented; }")
    wait_status = lambda text: page.wait_for_function('(text) => document.querySelector("#status").textContent === text', arg=text)

    assert order() == ['a', 'b', 'c']
    assert page.locator('.order-plan').all_text_contents() == ['Team', 'Plus', 'antigravity']
    assert page.locator('.provider-badge').all_text_contents() == ['Codex', 'Codex', 'Antigravity']
    assert page.locator('.order-position').all_text_contents() == ['1', '2', '3']
    assert page.locator('#account-count').inner_text() == '已关注 3 个账号'
    assert page.locator('#account-provider option').all_text_contents() == ['全部平台', 'Antigravity', 'Codex']
    assert row('a').get_by_role('button', name='上移 工作账号').is_disabled()
    assert row('c').get_by_role('button', name='下移 备用账号').is_disabled()
    assert page.locator('#save-accounts').is_disabled() and page.locator('#cancel-accounts').is_disabled()
    assert not unsaved() and page.locator('.dirty-pill:visible').count() == 0 and not unload_blocked()
    assert '/v1/setup/quota-refresh' not in calls and '/v1/setup/accounts' not in calls
    snapshot['quota']['accounts'][2]['plan'] = 'Google AI Pro'
    page.evaluate('readAccountPlans()')
    assert row('c').locator('.order-plan').inner_text() == 'Google AI Pro'
    snapshot['quota']['accounts'][2]['plan'] = ''
    page.evaluate('readAccountPlans()')
    assert row('c').locator('.order-plan').inner_text() == 'antigravity'
    results['unified_list_joins_plans_and_providers_by_id'] = True

    page.locator('#discover-accounts').click()
    page.wait_for_function("document.querySelectorAll('.account-row').length === 5")
    assert order() == ['a', 'b', 'c', 'd', 'e']
    assert page.locator('#account-count').inner_text() == '已关注 3 / 共 5 个账号'
    assert row('c').locator('.account-missing').inner_text() == '最近一次读取未在 CPAMP 中找到'
    assert row('d').get_attribute('data-watched') == 'false' and row('d').locator('.order-position').inner_text() == '–'
    assert row('d').locator('.order-plan').is_hidden() and row('d').locator('.account-file').inner_text() == 'codex-d.json'
    assert row('d').locator('.order-controls').evaluate('(n) => getComputedStyle(n).visibility') == 'hidden'
    assert '其中 2 个未关注' in page.locator('#status').inner_text() and '1 个已关注账号未在 CPAMP 中找到' in page.locator('#status').inner_text()
    assert page.locator('#save-accounts').is_disabled() and not unsaved()
    results['discovery_merges_unwatched_and_flags_missing_accounts'] = True

    search = page.locator('#account-search')
    for query, expected in (('备用', ['c']), ('plus', ['b']), ('ag-e', ['e']), ('antigravity', ['c', 'e'])):
        search.fill(query)
        assert order() == expected, (query, order())
    assert page.locator('#account-filter-status').inner_text().startswith('显示 2 / 5 个账号')
    search.fill('不存在')
    assert order() == [] and '没有匹配的账号' in page.locator('#account-filter-status').inner_text()
    page.get_by_role('button', name='清除筛选').click()
    assert order() == ['a', 'b', 'c', 'd', 'e'] and page.locator('#account-filter-status').is_hidden()
    assert page.evaluate('document.activeElement.id') == 'account-search'
    page.locator('#account-provider').select_option('antigravity')
    assert order() == ['c', 'e']
    page.locator('#account-watch').select_option('unwatched')
    assert order() == ['e']
    page.locator('#account-provider').select_option('')
    assert order() == ['d', 'e']
    page.locator('#account-watch').select_option('')
    results['search_provider_and_watch_filters'] = True

    page.locator('#account-provider').select_option('codex')
    assert order() == ['a', 'b', 'd']
    assert '只在可见的已关注账号之间调整' in page.locator('#account-filter-status').inner_text()
    row('b').get_by_role('button', name='上移 个人账号').click()
    assert order() == ['b', 'a', 'd']
    assert row('b').get_by_role('button', name='下移 个人账号').evaluate('(n) => n === document.activeElement')
    assert row('a').get_by_role('button', name='下移 工作账号').is_disabled()
    page.locator('#account-provider').select_option('')
    assert order() == ['b', 'a', 'c', 'd', 'e'] and page.locator('.order-position').all_text_contents()[:3] == ['1', '2', '3']
    assert page.locator('#save-accounts').is_enabled() and unsaved() == '未保存的更改：账号。离开或刷新页面前请先保存。'
    assert page.locator('[data-dirty-for=accounts]').is_visible() and unload_blocked()
    assert page.locator('#account-status').inner_text() == '有未保存的账号更改'
    page.locator('#cancel-accounts').click()
    assert order() == ['a', 'b', 'c', 'd', 'e'] and not saved and not unsaved() and not unload_blocked()
    assert page.locator('#account-status').inner_text() == '已恢复保存的账号设置'
    results['filtered_reorder_keeps_hidden_order_focus_and_cancel'] = True

    row('d').locator('[data-control=watch]').check()
    assert row('d').locator('.order-position').inner_text() == '4' and row('d').locator('.order-plan').inner_text() == '保存后同步套餐'
    row('d').locator('.alias-input').fill('新账号')
    assert row('d').locator('[data-control=watch]').get_attribute('aria-label') == '在壁纸上显示 新账号'
    assert row('d').get_by_role('button', name='上移 新账号').is_enabled()
    row('a').locator('[data-control=watch]').uncheck()
    assert order() == ['b', 'c', 'd', 'a', 'e']
    assert row('a').locator('[data-control=watch]').evaluate('(n) => n === document.activeElement')
    row('b').locator('.alias-input').fill('   ')
    state['save_failed'] = True
    page.locator('#save-accounts').click()
    wait_status('模拟保存失败')
    assert order() == ['b', 'c', 'd', 'a', 'e'] and page.locator('#save-accounts').is_enabled() and unload_blocked()
    state['save_failed'] = False
    page.locator('#save-accounts').click()
    page.wait_for_function("document.querySelector('#account-status').textContent === '账号设置已保存'")
    assert saved[-1] == {'accounts': [dict(id='b', alias='个人账号'), dict(id='c', alias='备用账号'), dict(id='d', alias='新账号')]}
    assert not unsaved() and not unload_blocked() and row('b').locator('.alias-input').input_value() == '个人账号'
    page.reload(wait_until='networkidle')
    assert order() == ['b', 'c', 'd'] and page.locator('#account-count').inner_text() == '已关注 3 个账号'
    results['watch_alias_fallback_save_failure_retry_and_reopen'] = True

    row('c').get_by_role('button', name='上移 备用账号').click()
    page.locator('#quota-refresh-minutes').fill('20')
    assert unsaved() == '未保存的更改：账号、用量自动刷新。离开或刷新页面前请先保存。'
    page.locator('#quota-refresh-minutes').fill('10')
    assert '用量自动刷新' not in unsaved()
    page.locator('#quota-refresh-minutes').fill('15')
    page.locator('#save-quota-refresh').click()
    wait_status('刷新设置已保存')
    assert cfg['quotaRefreshMinutes'] == 15 and order() == ['c', 'b', 'd']
    assert unsaved() == '未保存的更改：账号。离开或刷新页面前请先保存。'
    dialogs.clear()
    page.reload()
    assert any(kind == 'beforeunload' for kind, _ in dialogs), dialogs
    page.wait_for_load_state('networkidle')
    assert order() == ['b', 'c', 'd'] and page.locator('#quota-refresh-minutes').input_value() == '15'
    results['section_drafts_survive_other_saves_and_unload_prompts'] = True

    page.locator('#cpamp-url').fill('https://other.test')
    assert unsaved() == '未保存的更改：CPAMP 连接。离开或刷新页面前请先保存。'
    state['confirm'] = False
    writes = len(saved)
    page.locator('#save-cpamp').click()
    wait_status('已取消保存连接')
    assert dialogs[-1][0] == 'confirm' and '取消全部 3 个关注账号' in dialogs[-1][1] and len(saved) == writes
    page.locator('#cpamp-url').fill('https://example.test/')
    assert not unsaved()
    prompts = len(dialogs)
    page.locator('#save-cpamp').click()
    wait_status('已保存')
    assert len(dialogs) == prompts and saved[-1] == {'cpampUrl': 'https://example.test/'}
    page.locator('#clear-cpamp').click()
    wait_status('已取消清除凭据')
    assert '取消全部 3 个关注账号' in dialogs[-1][1] and len(saved) == writes + 1 and order() == ['b', 'c', 'd']
    state['confirm'] = True
    results['credential_changes_confirm_before_dropping_accounts'] = True

    assert page.locator('#save-sensors').is_disabled() and page.locator('#sensor-details').is_hidden()
    page.locator('#load-sensors').click()
    wait_status('传感器已检测')
    assert page.locator('#sensor-details').is_visible() and page.locator('#sensor-details').get_attribute('open') is None
    assert page.locator('#sensor-summary').inner_text() == '原始传感器读数（4 项）' and page.locator('#sensor-report').is_hidden()
    page.locator('#cpu-temp').select_option('cpu/0/temperature/0')
    assert unsaved() == '未保存的更改：硬件传感器。离开或刷新页面前请先保存。'
    page.locator('#save-sensors').click()
    wait_status('已保存')
    assert cfg['sensors'] == {'gpuId': None, 'cpuTemperatureId': 'cpu/0/temperature/0', 'gpuTemperatureId': None}
    assert not unsaved() and page.locator('#cpu-temp').input_value() == 'cpu/0/temperature/0'
    results['sensor_save_needs_detection_and_raw_report_is_folded'] = True

    facts = page.locator('#connection-facts').inner_text()
    assert 'Wallpaper Engine · 运行中 · 1600×2560 · 30 FPS' in facts and '构建 fixture-build' in facts and '无' in facts, facts
    assert '秒前' in facts or '刚刚' in facts
    assert page.locator('#advanced-diagnostics').get_attribute('open') is None and page.locator('#diagnostics').is_hidden()
    page.locator('#advanced-diagnostics summary').click()
    assert '"buildId": "fixture-build"' in page.locator('#diagnostics').inner_text()
    cfg['diagnostics'] = dict(cfg['diagnostics'], lastClient=dict(client, at=local_iso(900), paused=True), cacheFaults=['quota-cache'])
    page.evaluate('readDiagnostics()')
    facts = page.locator('#connection-facts').inner_text()
    assert '已暂停' in facts and '15 分钟前' in facts and '较久未上报' in facts and '1 项，详见高级诊断' in facts, facts
    assert page.locator('#connection-facts dd[data-state=warn]').count() == 2
    report['state'] = dict(status='partial', message='2/3 个账号已更新；其余保留上次快照', lastAttemptAt=local_iso(30), lastSuccessAt=local_iso(20),
                           accounts=[dict(alias='备用账号', status='ok', message='已更新 4 个额度窗口'), dict(alias='个人账号', status='failed', message='远端请求失败，保留上次快照'), dict(alias='新账号', status='ok', message='已更新 2 个额度窗口')])
    page.wait_for_function("document.querySelector('#quota-refresh-details-summary').textContent === '各账号结果：2 个已更新 · 1 个未更新'")
    assert page.locator('#quota-refresh-failures .result-row').all_text_contents() == ['个人账号 · 远端请求失败，保留上次快照']
    assert page.locator('#quota-refresh-details').get_attribute('open') is None and page.locator('#quota-refresh-accounts').is_hidden()
    page.locator('#quota-refresh-details summary').click()
    page.wait_for_timeout(3500)
    assert page.locator('#quota-refresh-details').get_attribute('open') is not None and page.locator('#quota-refresh-accounts .result-row').count() == 3
    results['readable_diagnostics_and_folded_refresh_results'] = True

    cfg['accounts'][0]['alias'] = '很长的账号别名' * 10
    snapshot['quota']['accounts'][0]['plan'] = '<img src=x onerror=alert(1)> Enterprise ' * 4
    page.reload(wait_until='networkidle')
    page.locator('#discover-accounts').click()
    page.wait_for_function("document.querySelectorAll('.account-row').length === 5")
    assert page.locator('#account-list img').count() == 0
    for width in (360, 540, 900, 1440):
        page.set_viewport_size({'width': width, 'height': 1000})
        assert page.locator('#account-list').evaluate('''list => {
            const bounds = list.getBoundingClientRect();
            return [...list.querySelectorAll('.watch-toggle,.alias-input,.account-meta,.order-plan,.order-controls')].filter(n => n.getClientRects().length).every(n => {
                const r = n.getBoundingClientRect(); return r.left >= bounds.left - 1 && r.right <= bounds.right + 1;
            });
        }'''), width
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'), width
    results['plain_text_metadata_and_four_responsive_widths'] = True

    for path in ('/admin/', '/?studio'):
        markup = context.request.get(BASE + path).text()
        assert 'rel="icon"' in markup and 'favicon.svg' in markup
    icon = context.request.get(page.locator('link[rel=icon]').evaluate('(n) => n.href'))
    assert icon.ok and 'image/svg+xml' in icon.headers['content-type'] and '<svg' in icon.text()
    results['admin_and_preview_favicon'] = True

    cfg['accounts'] = [dict(id='b', alias='个人账号', provider='codex'), dict(id='c', alias='备用账号', provider='antigravity'), dict(id='d', alias='新账号', provider='codex')]
    snapshot['quota']['accounts'][0]['plan'] = 'Plus'
    page.set_viewport_size({'width': 1080, 'height': 1000})
    page.reload(wait_until='networkidle')
    page.locator('#discover-accounts').click()
    page.wait_for_function("document.querySelectorAll('.account-row').length === 5")
    row('e').locator('[data-control=watch]').check()
    page.locator('#accounts-section').screenshot(path=str(OUT / 'admin-settings-accounts.png'))
    page.screenshot(path=str(OUT / 'admin-settings-page.png'), full_page=True)
    page.set_viewport_size({'width': 360, 'height': 900})
    page.locator('#accounts-section').screenshot(path=str(OUT / 'admin-settings-accounts-mobile.png'))
    page.locator('#cancel-accounts').click()
    cfg['accounts'] = []
    page.reload(wait_until='networkidle')
    assert page.locator('.account-row').count() == 0 and page.locator('#account-count').inner_text() == ''
    assert page.locator('#account-status').inner_text() == '还没有关注账号。点击「从 CPAMP 读取账号」，勾选要显示的账号后保存。'
    assert page.locator('#save-accounts').is_disabled() and page.locator('#refresh-quota-now').is_disabled()
    cfg['managementConfigured'] = False
    page.reload(wait_until='networkidle')
    assert page.locator('#account-status').inner_text() == '先保存 CPAMP 连接，再读取账号。'
    assert not errors, errors
    results['empty_states_and_no_browser_errors'] = True
    browser.close()

server.shutdown()
(OUT / 'admin-settings-result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Admin settings: {len(results)} checks passed; no browser errors; fixtures only.')
