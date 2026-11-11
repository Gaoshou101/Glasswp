"""Anonymous browser checks: complete admin fixture and persisted theme workflow."""
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
OUT = ROOT / 'artifacts/validation/themes'
OUT.mkdir(parents=True, exist_ok=True)

class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map, '.js': 'text/javascript', '.css': 'text/css'}
    def log_message(self, *_): pass

server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=str(ROOT / 'dist')))
threading.Thread(target=server.serve_forever, daemon=True).start()
BASE = f'http://127.0.0.1:{server.server_address[1]}'
report = dict(enabled=True, nextRefreshAt=None, state=dict(status='idle', message='尚未刷新', accounts=[]))
original = dict(preset='original', accent='#dcc6ff', surface='dark', background='assets/wallpaper-original.jpg')
cfg = dict(cpampUrl='https://example.test', managementConfigured=False, readToken='fixture-only', bridgeUrl=BASE,
           city=dict(id='1', name='苏州 · 江苏'), sensors={}, quotaRefreshEnabled=True, quotaRefreshMinutes=10,
           quotaRefresh=report, accounts=[], aiSources=dict(source='cpamp'), appearance=copy.deepcopy(original),
           clock=None, weather=dict(provider='open-meteo', apiKeyConfigured=False),
           diagnostics=dict(lastClient=None, lastSnapshotRead=None, cacheFaults=[], desktopInput=None))
state = dict(save_failed=False, expire_once=False)
errors, saves, results = [], [], {}

def respond(route):
    path = route.request.url.removeprefix(BASE)
    if path == '/v1/setup/session': route.fulfill(json=dict(session='fixture-session'))
    elif path == '/v1/setup/config/read': route.fulfill(json=cfg)
    elif path == '/v1/setup/quota-refresh/status': route.fulfill(json=report)
    elif path == '/v1/snapshot': route.fulfill(json=dict(quota=dict(accounts=[]), appearance=cfg.get('appearance')))
    elif path == '/v1/setup/config':
        assert route.request.headers['x-wallpaper-admin'] == 'fixture-session'
        assert route.request.headers['x-wallpaper-setup'] == '1'
        if state['expire_once']:
            state['expire_once'] = False
            route.fulfill(status=403, json=dict(message='模拟会话过期'))
        elif state['save_failed']: route.fulfill(status=500, json=dict(message='模拟保存失败'))
        else:
            body = route.request.post_data_json
            saves.append(copy.deepcopy(body)); cfg.update(body)
            route.fulfill(json=dict(message='主题设置已保存'))
    else: raise AssertionError(f'Unexpected API request: {path}')

try:
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
        context = browser.new_context(viewport=dict(width=1440, height=1080))
        page = context.new_page()
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.on('dialog', lambda dialog: dialog.accept())
        page.route('**/v1/**', respond)
        def loaded():
            page.wait_for_function("document.querySelector('#appearance-status').textContent === '主题设置已读取。'")
            page.wait_for_function("document.querySelector('#bridge-state').dataset.state === 'ok'")
        def changed(): return page.locator('#save-appearance').is_enabled()
        def unload_blocked():
            return page.evaluate("() => { const e = new Event('beforeunload', {cancelable:true}); dispatchEvent(e); return e.defaultPrevented; }")
        def save():
            page.locator('#save-appearance').click()
            page.wait_for_function("document.querySelector('#appearance-status').textContent === '主题设置已保存'")
            assert not changed()

        page.goto(BASE + '/admin/', wait_until='networkidle'); loaded()
        assert page.locator('#wallpaper-theme-preset option').evaluate_all('(o) => o.map(x=>x.value)') == ['original', 'neon', 'pool']
        assert page.locator('#admin-theme-mode').input_value() == 'follow'
        assert not changed() and not unload_blocked() and not saves
        results['opens_without_writing_global_settings'] = True
        for preset, color, surface in [('original', '#dcc6ff', 'dark'), ('neon', '#e5f34d', 'dark'), ('pool', '#147f91', 'light')]:
            page.locator('#wallpaper-theme-preset').select_option(preset)
            assert page.locator('#wallpaper-theme-accent').input_value() == color
            assert page.locator('#wallpaper-theme-surface').input_value() == surface
            assert page.locator('html').get_attribute('data-admin-theme') == surface
        assert changed() and unload_blocked()
        page.locator('#cancel-appearance').click()
        assert page.locator('#wallpaper-theme-preset').input_value() == 'original' and not changed()
        results['shared_presets_and_cancel_restore_saved_palette'] = True

        for mode in ('light', 'dark', 'follow'):
            page.locator('#admin-theme-mode').select_option(mode)
            expected = mode if mode != 'follow' else 'dark'
            assert page.locator('html').get_attribute('data-admin-theme') == expected
            if changed(): save()
            page.reload(wait_until='networkidle'); loaded()
            assert page.locator('#admin-theme-mode').input_value() == mode
            assert page.locator('html').get_attribute('data-admin-theme') == expected
        results['three_admin_modes_survive_refresh'] = True
        page.locator('#wallpaper-theme-preset').select_option('pool'); save()
        page.reload(wait_until='networkidle'); loaded()
        assert page.locator('#wallpaper-theme-preset').input_value() == 'pool'
        assert page.locator('html').get_attribute('data-admin-theme') == 'light'
        assert 'assets/wallpapers/pool.jpg' in page.locator('html').evaluate('(n)=>n.style.getPropertyValue("--admin-background")')
        results['follow_tracks_saved_wallpaper_surface_and_background'] = True

        page.locator('#wallpaper-theme-preset').select_option('neon'); state['save_failed'] = True
        page.locator('#save-appearance').click()
        page.wait_for_function("document.querySelector('#appearance-status').textContent.includes('模拟保存失败')")
        assert changed() and cfg['appearance']['preset'] == 'pool' and unload_blocked()
        page.locator('#cancel-appearance').click()
        assert page.locator('#wallpaper-theme-preset').input_value() == 'pool' and not changed()
        state['save_failed'] = False
        page.locator('#wallpaper-theme-preset').select_option('neon'); state['expire_once'] = True
        save(); assert cfg['appearance']['preset'] == 'neon'
        results['save_failure_retains_draft_and_expired_session_retries'] = True
        cfg['appearance']['background'] = 'assets/wallpaper-original.jpg'
        page.reload(wait_until='networkidle'); loaded()
        page.locator('#wallpaper-theme-accent').evaluate("n => {n.value='#cad636';n.dispatchEvent(new Event('input',{bubbles:true}))}")
        save(); assert cfg['appearance']['background'] == 'assets/wallpaper-original.jpg'
        results['color_edit_preserves_custom_background'] = True

        for width in (360, 540, 900, 1440):
            page.set_viewport_size(dict(width=width, height=1080))
            for mode in ('light', 'dark', 'follow'):
                page.locator('#admin-theme-mode').select_option(mode)
                assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'), (width, mode)
                assert page.locator('#appearance-section').evaluate("n => [...n.querySelectorAll('input,select,button')].every(x=>{const r=x.getBoundingClientRect(),b=n.getBoundingClientRect();return r.left>=b.left&&r.right<=b.right})"), (width, mode)
        if changed(): page.locator('#cancel-appearance').click()
        results['three_modes_fit_four_responsive_widths'] = True
        page.set_viewport_size(dict(width=1440, height=1080))
        for mode in ('light', 'dark', 'follow'):
            page.locator('#admin-theme-mode').select_option(mode)
            page.wait_for_timeout(300)  # Allow the control color transitions to finish before capture.
            page.locator('#appearance-section').screenshot(path=str(OUT / f'admin-{mode}.png'))
        if changed(): page.locator('#cancel-appearance').click()
        cfg.pop('appearance'); page.reload(wait_until='networkidle'); loaded()
        assert page.locator('#wallpaper-theme-preset').input_value() == 'original' and not changed()
        assert not errors, errors
        results['old_bridge_defaults_and_zero_browser_errors'] = True
        browser.close()
finally: server.shutdown()
(OUT / 'admin-theme-result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Admin theme: {len(results)} checks passed; anonymous fixtures; no browser errors.')
