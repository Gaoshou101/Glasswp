"""AI source editor acceptance with anonymous fixtures and an ephemeral local server."""
import copy
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


class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=str(ROOT / 'public')))
threading.Thread(target=server.serve_forever, daemon=True).start()
BASE = f'http://127.0.0.1:{server.server_address[1]}'
report = dict(enabled=False, state=dict(status='idle', message='尚未刷新', accounts=[]))
cfg = dict(clock=None, weather=dict(provider='open-meteo', apiKeyConfigured=False), cpampUrl='https://example.test',
           managementConfigured=False, readToken='fixture-only', bridgeUrl=BASE, city=dict(id='1', name='测试城市'), sensors={},
           quotaRefreshEnabled=True, quotaRefreshMinutes=10, quotaRefresh=report, accounts=[], diagnostics={},
           aiSources=dict(source='cpamp',
                          magpieUrl='http://127.0.0.1:3425', magpieKeyConfigured=False))
data = dict(status='unconfigured', message='尚未启用本机来源', accounts=[])
saved, calls, errors, results = [], [], [], {}
state = dict(fail=False)


def respond(route):
    path = route.request.url.removeprefix(BASE)
    calls.append(path)
    if path == '/v1/setup/session':
        route.fulfill(json=dict(session='fixture-session'))
    elif path == '/v1/setup/config/read':
        route.fulfill(json=cfg)
    elif path in ('/v1/setup/quota-refresh/status', '/v1/setup/ai-sources/refresh-status'):
        route.fulfill(json=report)
    elif path == '/v1/snapshot':
        route.fulfill(json=dict(quota=dict(accounts=[])))
    elif path == '/v1/setup/ai-sources/status':
        route.fulfill(json=data)
    elif path == '/v1/setup/ai-sources/refresh':
        route.fulfill(json=dict(message='已请求刷新已保存的来源'))
    elif path == '/v1/setup/config':
        if state['fail']:
            route.fulfill(status=500, json=dict(message='模拟保存失败'))
            return
        body = route.request.post_data_json
        saved.append(copy.deepcopy(body))
        if 'clock' in body:
            cfg['clock'] = body['clock']
        if 'aiSources' in body:
            previous = cfg['aiSources']
            source = body['aiSources']
            cfg['aiSources'] = {key: source[key] for key in ('source', 'magpieUrl', 'localOptions', 'magpieOptions', 'cpaUrl', 'cpaApiVersion', 'cpaOptions', 'sub2apiUrl', 'sub2apiOptions')}
            cfg['aiSources']['magpieKeyConfigured'] = bool(source.get('magpieApiKey')) or (previous['magpieKeyConfigured']
                and source['magpieUrl'] == previous['magpieUrl'] and not source.get('clearMagpieApiKey'))
            for kind in ('cpa', 'sub2api'):
                key = kind + 'Key'
                clear = 'clear' + kind[0].upper() + kind[1:] + 'Key'
                cfg['aiSources'][kind + 'KeyConfigured'] = bool(source.get(key)) or (previous.get(kind + 'KeyConfigured', False)
                    and source[kind + 'Url'] == previous.get(kind + 'Url', '') and not source.get(clear))
                if source[kind + 'Url'] != previous.get(kind + 'Url', '') or source.get(key) or source.get(clear):
                    cfg['aiSources'][kind + 'Options']['accounts'] = None
        route.fulfill(json=dict(message='AI 来源已保存'))
    else:
        raise AssertionError(path)


try:
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
        page = browser.new_page(viewport=dict(width=1200, height=1000))
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.on('dialog', lambda dialog: dialog.accept())
        page.route('**/v1/**', respond)
        page.goto(BASE + '/admin/', wait_until='networkidle')
        page.wait_for_function('config !== null && !aiSourcesLoading')
        assert page.locator('#save-ai-sources').is_disabled() and page.locator('#refresh-ai-sources').is_disabled()
        assert page.locator('#ai-magpie-fields').is_hidden()
        assert not cfg['managementConfigured']

        assert page.locator('#ai-source').input_value() == 'cpamp'
        assert page.locator('#ai-source option').evaluate_all('(options) => options.map(o => o.value)') == ['cpamp', 'local', 'magpie', 'cpa', 'sub2api']
        assert page.locator('#ai-codex-enabled, #ai-claude-enabled, #ai-magpie-enabled').count() == 0
        assert page.locator('#cpamp-section').is_visible()
        page.locator('#ai-source').select_option('local')
        assert page.locator('#save-ai-sources').is_enabled() and 'AI 来源' in page.locator('#unsaved').inner_text()
        assert page.locator('#refresh-ai-sources').is_disabled()
        assert cfg['aiSources']['source'] == 'cpamp' and page.locator('#cpamp-section').is_hidden()
        assert '当前生效：CPAMP' in page.locator('#ai-sources-current-source').inner_text()
        assert '自动检测本机支持的 AI agent' in page.locator('#ai-source-help').inner_text()
        page.locator('#save-clock').click()
        page.wait_for_function('config.clock !== null')
        assert page.locator('#ai-source').input_value() == 'local'
        page.locator('#save-ai-sources').click()
        page.wait_for_function('config.aiSources.source === "local" && !aiSourcesLoading')
        assert list(saved[-1]) == ['aiSources'] and saved[-1]['aiSources']['source'] == 'local'
        assert set(saved[-1]['aiSources']) == {'source', 'magpieUrl', 'localOptions', 'magpieOptions', 'cpaUrl', 'cpaApiVersion', 'cpaOptions', 'sub2apiUrl', 'sub2apiOptions'}
        assert page.locator('#save-ai-sources').is_disabled() and page.locator('#refresh-ai-sources').is_enabled()
        assert page.locator('#refresh-quota-now').is_disabled()
        assert cfg['accounts'] == [] and not cfg['managementConfigured']
        results['exclusive_selector_local_without_cpamp_and_drafts_only_apply_on_save'] = True

        page.locator('#ai-source').select_option('magpie')
        assert page.locator('#ai-magpie-fields').is_visible() and page.locator('#ai-magpie-key').get_attribute('type') == 'password'
        before = len(saved)
        for value in ('http://remote.example.test:3425', 'https://user:pass@example.test', 'https://example.test/?secret=x', 'https://example.test/path'):
            page.locator('#ai-magpie-url').fill(value)
            page.locator('#save-ai-sources').click()
            page.wait_for_function('document.getElementById("status").textContent.includes("Magpie 地址须为")')
            assert len(saved) == before
        page.locator('#ai-magpie-url').fill('https://magpie.example.test')
        page.locator('#ai-magpie-key').fill('fixture-readonly-key')
        page.locator('#save-ai-sources').click()
        page.wait_for_function('config.aiSources.magpieKeyConfigured && !aiSourcesLoading')
        assert saved[-1]['aiSources']['magpieApiKey'] == 'fixture-readonly-key'
        assert page.locator('#ai-magpie-key').input_value() == ''
        assert 'fixture-readonly-key' not in page.locator('body').inner_text()
        results['magpie_url_validation_and_masked_write_only_key'] = True

        page.locator('#ai-magpie-key').fill('fixture-replacement')
        page.locator('#ai-magpie-url').fill('https://other.example.test')
        assert page.locator('#ai-magpie-key').input_value() == ''
        before = len(saved)
        page.locator('#save-ai-sources').click()
        page.wait_for_function('document.getElementById("status").textContent.includes("请填写新密钥")')
        assert len(saved) == before
        page.locator('#ai-magpie-clear-key').check()
        assert page.locator('#ai-magpie-key').is_disabled()
        page.locator('#save-ai-sources').click()
        page.wait_for_function('config.aiSources.magpieUrl === "https://other.example.test" && !aiSourcesLoading')
        assert saved[-1]['aiSources']['clearMagpieApiKey'] and not cfg['aiSources']['magpieKeyConfigured']
        assert 'magpieApiKey' not in saved[-1]['aiSources']
        results['changing_url_never_reuses_or_sends_old_key'] = True

        page.locator('#ai-source').select_option('local')
        state['fail'] = True
        page.locator('#save-ai-sources').click()
        page.wait_for_function('document.getElementById("status").textContent.includes("模拟保存失败")')
        assert page.locator('#ai-source').input_value() == 'local' and cfg['aiSources']['source'] == 'magpie'
        assert page.locator('#save-ai-sources').is_enabled()
        state['fail'] = False
        page.locator('#cancel-ai-sources').click()
        assert page.locator('#ai-source').input_value() == 'magpie' and page.locator('#save-ai-sources').is_disabled()
        results['failed_save_keeps_draft_and_cancel_restores_saved_state'] = True

        data.update(status='ok', message='已检测到 2 个账号', accounts=[
            dict(id='local:codex:test', alias='本机 Codex', provider='codex', plan='GPT Plus', windows=[{}]),
            dict(id='magpie:test', alias='<img src=x onerror=alert(1)>', provider='claude', plan='<script>fixture</script>', windows=[]),
        ])
        page.locator('#ai-source').select_option('local')
        page.locator('#refresh-ai-sources').click()
        page.wait_for_function('document.querySelectorAll("#ai-sources-accounts li").length === 2')
        assert page.locator('#ai-sources-accounts img, #ai-sources-accounts script').count() == 0
        assert 'GPT Plus' in page.locator('#ai-sources-accounts').inner_text()
        assert page.locator('#ai-source').input_value() == 'local' and cfg['aiSources']['source'] == 'magpie'
        assert '/v1/setup/ai-sources/refresh' in calls and '/v1/setup/quota-refresh' not in calls
        assert '/v1/setup/accounts' not in calls and page.locator('#account-list li').count() == 0
        results['safe_detected_list_and_refresh_use_saved_sources_without_cpamp'] = True

        page.locator('#cancel-ai-sources').click()
        assert page.locator('.nav a[href="#magpie-settings-section"]').is_visible()
        assert page.locator('#magpie-settings-section').is_visible()
        assert page.locator('#local-settings-section').is_hidden()
        page.locator('#magpie-all-accounts').uncheck()
        editor = page.locator('#magpie-account-editor')
        editor.locator('input[type=text]').first.fill('我的主账号')
        editor.get_by_role('button', name='下移 本机 Codex', exact=True).click()
        assert editor.locator('li').first.get_attribute('data-account-id') == 'magpie:test'
        editor.locator('li[data-account-id="magpie:test"]').locator('input[type=checkbox]').uncheck()
        page.locator('#magpie-auto-refresh').uncheck()
        page.locator('#magpie-refresh-minutes').fill('7')
        page.locator('#save-magpie-settings').click()
        page.wait_for_function('config.aiSources.magpieOptions?.refreshMinutes === 7 && !aiSourcesLoading')
        selected = cfg['aiSources']['magpieOptions']
        assert selected == dict(autoRefreshEnabled=False, refreshMinutes=7, accounts=[dict(id='local:codex:test', alias='我的主账号')]), selected
        assert cfg['aiSources']['localOptions'] == dict(autoRefreshEnabled=True, refreshMinutes=10, accounts=None)
        page.locator('#magpie-refresh-minutes').fill('0')
        before = len(saved)
        page.locator('#save-magpie-settings').click()
        assert len(saved) == before and '1–10080' in page.locator('#status').inner_text()
        page.locator('#cancel-magpie-settings').click()
        assert page.locator('#magpie-refresh-minutes').input_value() == '7'
        page.reload(wait_until='networkidle')
        page.wait_for_function('config !== null && !aiSourcesLoading')
        assert not page.locator('#magpie-auto-refresh').is_checked()
        assert not page.locator('#magpie-all-accounts').is_checked()
        assert page.locator('#magpie-account-editor input[type=text]').first.input_value() == '我的主账号'
        results['source_menu_selection_alias_order_cadence_reload_and_validation'] = True

        # Source switching preserves the other source's saved credential and CPAMP account list.
        cfg.update(managementConfigured=True, accounts=[dict(id='saved-cpamp', alias='已关注账号', provider='codex')])
        cfg['aiSources']['magpieKeyConfigured'] = True
        page.evaluate('readConfig()')
        page.locator('#ai-source').select_option('cpamp')
        page.locator('#save-ai-sources').click()
        page.wait_for_function('config.aiSources.source === "cpamp" && !aiSourcesLoading')
        assert cfg['aiSources']['magpieKeyConfigured'] and cfg['accounts'][0]['id'] == 'saved-cpamp'
        assert 'magpieApiKey' not in saved[-1]['aiSources'] and 'clearMagpieApiKey' not in saved[-1]['aiSources']
        assert page.locator('#cpamp-section').is_visible() and page.locator('#accounts-section').is_visible()
        assert page.locator('#refresh-quota-now').is_enabled() and page.locator('#refresh-ai-sources').is_disabled()
        assert page.locator('#ai-sources-accounts').is_hidden()
        assert '当前使用 CPAMP' in page.locator('#ai-sources-status').inner_text()
        page.locator('#ai-source').select_option('magpie')
        page.locator('#save-ai-sources').click()
        page.wait_for_function('config.aiSources.source === "magpie" && !aiSourcesLoading')
        assert cfg['managementConfigured'] and len(cfg['accounts']) == 1
        assert cfg['aiSources']['magpieKeyConfigured'] and page.locator('#refresh-quota-now').is_disabled()
        results['switching_sources_preserves_credentials_accounts_and_refresh_is_exclusive'] = True

        for kind, label in [('cpa', 'CPA'), ('sub2api', 'Sub2API')]:
            page.locator('#ai-source').select_option(kind)
            assert page.locator(f'#{kind}-settings-section').is_visible()
            assert page.locator(f'.nav a[href="#{kind}-settings-section"]').is_visible()
            assert page.locator('#cpamp-section').is_hidden()
            assert page.locator(f'#refresh-{kind}-settings').is_disabled()
            page.locator(f'#ai-{kind}-url').fill('http://remote.example.test')
            before = len(saved)
            page.locator(f'#save-{kind}-settings').click()
            assert len(saved) == before and label in page.locator('#status').inner_text()
            page.locator(f'#ai-{kind}-url').fill(f'https://{kind}.example.test')
            page.locator(f'#ai-{kind}-key').fill(f'{kind}-fixture-key')
            if kind == 'cpa':
                page.locator('#ai-cpa-version').select_option('v8')
            page.locator(f'#{kind}-refresh-minutes').fill('13')
            data.update(accounts=[dict(id=f'{kind}:test', alias=f'{label} 测试账号', provider='codex', plan='GPT Plus', windows=[{}])])
            page.locator(f'#save-{kind}-settings').click()
            page.wait_for_function(f'config.aiSources.source === "{kind}" && !aiSourcesLoading')
            assert saved[-1]['aiSources'][kind + 'Key'] == f'{kind}-fixture-key'
            assert cfg['aiSources'][kind + 'KeyConfigured']
            assert page.locator(f'#ai-{kind}-key').input_value() == ''
            assert f'{kind}-fixture-key' not in page.locator('body').inner_text()
            assert page.locator(f'#refresh-{kind}-settings').is_enabled()
            page.locator(f'#{kind}-all-accounts').uncheck()
            page.locator(f'#{kind}-account-editor input[type=text]').fill(f'{label} 别名')
            page.locator(f'#save-{kind}-settings').click()
            page.wait_for_function('!aiSourcesLoading && !aiSourcesDirty()')
            page.reload(wait_until='networkidle')
            page.wait_for_function('config !== null && !aiSourcesLoading')
            assert page.locator(f'#{kind}-account-editor input[type=text]').input_value() == f'{label} 别名'
            page.locator(f'#ai-{kind}-url').fill(f'https://new-{kind}.example.test')
            before = len(saved)
            page.locator(f'#save-{kind}-settings').click()
            assert len(saved) == before and '新密钥' in page.locator('#status').inner_text()
            page.locator(f'#ai-{kind}-clear-key').check()
            page.locator(f'#save-{kind}-settings').click()
            page.wait_for_function('!aiSourcesLoading && !aiSourcesDirty()')
            assert not cfg['aiSources'][kind + 'KeyConfigured'] and cfg['aiSources'][kind + 'Options']['accounts'] is None
            assert cfg['aiSources']['magpieKeyConfigured'] and len(cfg['accounts']) == 1
            for width in (1200, 390):
                page.set_viewport_size(dict(width=width, height=1000))
                assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
                page.locator(f'#{kind}-settings-section').screenshot(path=str(OUT / f'{kind}-settings-{width}.png'))
        results['cpa_sub2api_menus_credentials_versions_options_reload_and_origin_isolation'] = True
        page.locator('#ai-source').select_option('magpie')
        page.locator('#save-ai-sources').click()
        page.wait_for_function('config.aiSources.source === "magpie" && !aiSourcesLoading')

        for width in (1200, 390):
            page.set_viewport_size(dict(width=width, height=1000))
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
            page.locator('#magpie-settings-section').screenshot(path=str(OUT / f'ai-sources-admin-{width}.png'))
        assert not errors, errors
        results['desktop_mobile_layout_and_no_browser_errors'] = True
        browser.close()
finally:
    server.shutdown()

(OUT / 'ai-sources-admin-result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'AI sources admin: {len(results)} checks passed; fixtures only; no browser errors.')
