"""Render a fresh, anonymous preview from the production package."""
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'artifacts/workshop'
now = time.time()
iso = lambda seconds: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(seconds))
metric = lambda value, unit='%': dict(value=value, unit=unit, source='preview-example', sensorId=None)
device = lambda name, load, temp, used=None, total=None: dict(name=name, load=metric(load), temperature=metric(temp, '°C'), usedBytes=used, totalBytes=total)
window = lambda ident, label, remaining, seconds, duration: dict(id=ident, label=label, remainingPercent=remaining, usedValue=None, limitValue=None, quotaUnit='', resetAt=iso(now+seconds), durationSeconds=duration, mode='fixed', accuracy='exact', observedAt=iso(now), stale=False)
fixture = dict(schemaVersion=1, generatedAt=iso(now),
    hardware=dict(status='ok', observedAt=iso(now), cpu=device('CPU', 18, 48), gpu=device('GPU', 12, 43), vram=device('专用显存', 25, None, 4*1024**3, 16*1024**3), ram=device('物理内存', 38, None, 18.24*1024**3, 48*1024**3), message='示例数据'),
    weather=dict(status='ok', fetchedAt=iso(now), observedAt=iso(now), city='苏州', temperature=23, low=19, high=26, code=2, isDay=True, message='示例天气'),
    quota=dict(status='ok', fetchedAt=iso(now), message='预览示例', accounts=[dict(id=f'example-{i}', alias=f'示例账号 {letter}', provider='codex', plan=plan, windows=[window('short','5 小时窗口',remaining,7200,18000),window('week','周窗口',weekly,259200,604800)]) for i, letter, plan, remaining, weekly in [(1,'A','Pro',84,67),(2,'B','Team',96,81),(3,'C','Plus',72,45)]]))
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport=dict(width=400,height=640))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps(dict(version=1,readToken='preview-example',rotateSeconds=0))) + ');')
    page = context.new_page()
    errors = []
    page.on('pageerror', lambda error: errors.append(str(error)))
    page.route('http://127.0.0.1:17632/**', lambda route: route.fulfill(json=fixture) if route.request.url.endswith('/v1/snapshot') else route.fulfill(json=dict(canRefresh=False,enabled=False,status='idle')))
    page.goto((OUT / 'index.html').as_uri(), wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    page.wait_for_function("document.querySelector('.note').hidden")
    assert page.locator('.quota-card').count() == 3
    assert page.locator('.art').evaluate('image => image.complete && image.naturalWidth > 0')
    assert not errors, errors
    page.screenshot(path=str(OUT / 'preview.png'))
    browser.close()
print('Anonymous production preview ready:', (OUT / 'preview.png').stat().st_size, 'bytes; 0 browser errors.')
