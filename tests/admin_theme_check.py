"""Anonymous browser checks for the reusable wallpaper and admin themes."""
import functools
import http.server
import json
import sys
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / '.tools/python'))
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(ROOT / 'dist')))
threading.Thread(target=server.serve_forever, daemon=True).start()
base = f'http://127.0.0.1:{server.server_address[1]}'
saved = {'appearance': {'preset': 'original', 'accent': '#dcc6ff', 'surface': 'dark', 'background': 'assets/wallpaper-original.jpg'}}

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    page = browser.new_page(viewport={'width': 900, 'height': 900})
    def setup(route):
        path = route.request.url.split('/v1/setup/')[-1].split('?')[0]
        if path == 'session': route.fulfill(json={'session': 'fixture-session'})
        elif path == 'config/read': route.fulfill(json=saved)
        elif path == 'config':
            body = json.loads(route.request.post_data or '{}')
            saved.update(body); route.fulfill(json={'message': '主题设置已保存'})
        else: route.fulfill(json={})
    page.route('**/v1/setup/**', setup)
    page.goto(base + '/admin/', wait_until='networkidle')
    page.wait_for_selector('#appearance-section')
    assert page.locator('#admin-theme-mode').input_value() == 'follow'
    for preset, expected in [('original', '#dcc6ff'), ('neon', '#e5f34d'), ('pool', '#147f91')]:
        page.locator('#wallpaper-theme-preset').select_option(preset)
        assert page.locator('#wallpaper-theme-accent').input_value() == expected
        assert page.locator('#appearance-preview').is_visible()
    page.locator('#wallpaper-theme-preset').select_option('pool')
    page.locator('#admin-theme-mode').select_option('light')
    assert page.locator('html').get_attribute('data-admin-theme') == 'light'
    assert page.locator('#save-appearance').is_enabled()
    page.locator('#save-appearance').click()
    page.wait_for_timeout(150)
    assert page.locator('#save-appearance').is_disabled()
    page.locator('#admin-theme-mode').select_option('dark')
    assert page.locator('html').get_attribute('data-admin-theme') == 'dark'
    page.locator('#cancel-appearance').click()
    assert page.locator('#admin-theme-mode').input_value() == 'light'
    page.set_viewport_size({'width': 360, 'height': 900})
    assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
    browser.close()
server.shutdown()
print('Admin theme: 3 presets, 3 admin modes, save/cancel and mobile layout passed')
