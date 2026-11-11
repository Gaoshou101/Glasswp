"""Production UI checks for native wallet, unlimited and unknown quota semantics."""
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'artifacts/validation/ai-wallet'
OUT.mkdir(parents=True, exist_ok=True)
BASE = 'http://127.0.0.1:17632'
now = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
metric = dict(value=20, unit='%', source='fixture', sensorId=None)
device = dict(name='Fixture', load=metric, temperature=metric, usedBytes=None, totalBytes=None)
window = dict(id='wallet', label='可用积分', remainingPercent=None, usedValue=None, limitValue=None, quotaUnit='points', resetAt=None, durationSeconds=None, mode='non_window', accuracy='unknown', observedAt=now, stale=False)
accounts = [dict(id='wallet', alias='CatPaw 本机', provider='catpaw', plan='Pro', windows=[dict(window, remainingValue=1234.5)]),
            dict(id='unlimited', alias='Copilot 本机', provider='github-copilot', plan='Pro', windows=[dict(window, id='unlimited', label='对话', quotaUnit='', displayText='不限量')]),
            dict(id='zero', alias='科搭 本机', provider='codar', plan='', windows=[dict(window, remainingValue=0)])]
snapshot = dict(schemaVersion=1, generatedAt=now,
    hardware=dict(status='ok', observedAt=now, message='', **{k:device for k in ['cpu','gpu','vram','ram']}),
    weather=dict(status='ok', fetchedAt=now, observedAt=now, city='Fixture', temperature=23, low=19, high=26, code=0, isDay=True, message=''),
    quota=dict(status='ok', fetchedAt=now, message='', accounts=accounts))
errors = []
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport=dict(width=1600, height=2560))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps(dict(version=2, readToken='anonymous', rotateSeconds=0, quotaLayout='cards', motion=False))) + ');')
    page = context.new_page()
    page.on('pageerror', lambda error: errors.append(str(error)))
    def respond(route):
        if route.request.url.endswith('/v1/snapshot'):
            route.fulfill(json=snapshot)
        elif route.request.url.endswith('/v1/desktop-input'):
            route.fulfill(json=dict(available=True, allowed=True))
        else:
            route.fulfill(json=dict(canRefresh=False, refresh=dict(state=dict(status='idle', message='', lastFinishedAt=None))))
    page.route(BASE + '/**', respond)
    page.goto((ROOT / 'dist/index.html').as_uri(), wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert '1,234.5 points' in page.locator('[data-account-id="wallet"] .remaining').inner_text()
    assert '不限量' in page.locator('[data-account-id="unlimited"] .remaining').inner_text()
    assert '0 points' in page.locator('[data-account-id="zero"] .remaining').inner_text()
    assert page.locator('.quota-card .track:visible').count() == 0
    for width, height in [(1600,2560), (800,1280)]:
        page.set_viewport_size(dict(width=width, height=height))
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        assert page.locator('.remaining').evaluate_all('nodes=>nodes.every(n=>n.scrollWidth<=n.clientWidth+1)')
        page.locator('.quota').screenshot(path=str(OUT / f'wallet-{width}.png'))
    assert not errors, errors
    browser.close()
print('AI wallet browser checks passed: amount, zero, unlimited, no percentage track, portrait sizes')
