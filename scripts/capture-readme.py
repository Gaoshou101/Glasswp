"""Capture actual Glasswp UI with anonymous fixtures, never a live bridge.

Run after ``npm run build``. Requires Playwright and the locally installed Chrome.
The browser composes and encodes the screenshots; no image editor is required.
"""
import base64
from datetime import datetime, timezone
import functools
import http.server
import json
import os
from pathlib import Path
import sys
import threading

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
if sys.version_info[:2] == (3, 11) and (ROOT / '.tools/python311').is_dir():
    sys.path.insert(0, str(ROOT / '.tools/python311'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'docs/images'
OUT.mkdir(parents=True, exist_ok=True)
STAMP = datetime(2026, 11, 12, 2, 24, tzinfo=timezone.utc)
NOW = STAMP.timestamp()
iso = lambda offset=0: datetime.fromtimestamp(NOW + offset, timezone.utc).isoformat()
metric = lambda value, unit='%': dict(value=value, unit=unit, source='readme-example', sensorId=None)


def device(name, load, temperature=None, used=None, total=None):
    return dict(name=name, load=metric(load), temperature=metric(temperature, '°C'), usedBytes=used, totalBytes=total)


def window(ident, label, value, reset, duration):
    return dict(id=ident, label=label, remainingPercent=value, usedValue=None, limitValue=None, quotaUnit='',
                resetAt=iso(reset), durationSeconds=duration, mode='fixed', accuracy='exact', observedAt=iso(), stale=False)


CLOCK = dict(timezone='Asia/Shanghai', offsetSeconds=0, seconds=False, hour12=False,
             worldZones=['Asia/Shanghai', 'Asia/Tokyo', 'Asia/Singapore', 'Europe/London', 'America/New_York', 'America/Los_Angeles'])
THEMES = [
    ('Glasswp-Purple', dict(preset='original', accent='#dcc6ff', surface='dark', background='assets/wallpaper-original.jpg')),
    ('Glasswp-Angel', dict(preset='neon', accent='#e5f34d', surface='dark', background='assets/wallpapers/neon.jpg')),
    ('Glasswp-Corgi', dict(preset='pool', accent='#147f91', surface='light', background='assets/wallpapers/pool.jpg')),
]
ACCOUNTS = [dict(id='example-' + provider, alias=alias, provider=provider, plan=plan,
                 windows=[window('short', '5 小时', short, 7200, 18000), window('week', '7 天', week, 259200, 604800)])
            for provider, alias, plan, short, week in [('codex', 'Codex 示例', 'Plus', 84, 67),
                                                       ('claude', 'Claude 示例', 'Pro', 72, 91),
                                                       ('antigravity', 'Gemini 示例', 'Pro', 96, 78)]]
SNAPSHOT = dict(schemaVersion=1, generatedAt=iso(), clock=CLOCK,
    hardware=dict(status='ok', observedAt=iso(), message='匿名示例',
                  cpu=device('CPU', 18, 48), gpu=device('GPU', 12, 43),
                  vram=device('专用显存', 25, used=4 * 1024**3, total=16 * 1024**3),
                  ram=device('物理内存', 38, used=12.16 * 1024**3, total=32 * 1024**3)),
    quota=dict(status='ok', fetchedAt=iso(), message='匿名示例', accounts=ACCOUNTS),
    weather=dict(status='ok', fetchedAt=iso(), observedAt=iso(), city='苏州', temperature=23, low=19, high=26,
                 code=2, isDay=True, message='', provider='open-meteo', timezone='Asia/Shanghai',
                 hourly=[dict(time=iso(hour * 3600), temperature=23 - hour * .45,
                              precipitationProbability=0 if hour < 6 else 35 + hour * 2,
                              precipitationMm=0 if hour < 6 else .8, code=2 if hour < 6 else 61) for hour in range(12)]))


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map, '.js': 'text/javascript', '.css': 'text/css'}
    def log_message(self, *_): pass


def data_image(content):
    return 'data:image/png;base64,' + base64.b64encode(content).decode()


def encode(page, name):
    """Encode the browser's own screenshot as WebP using a browser canvas."""
    raw = data_image(page.screenshot())
    encoded = page.evaluate('''async src => {
      const img = new Image(); img.src = src; await img.decode();
      const canvas = document.createElement('canvas'); canvas.width=img.width; canvas.height=img.height;
      canvas.getContext('2d').drawImage(img,0,0);
      return canvas.toDataURL('image/webp', .89).split(',')[1];
    }''', raw)
    (OUT / name).write_bytes(base64.b64decode(encoded))


def composition(page, css, body):
    page.set_content('<html lang="zh-CN"><meta charset="utf-8"><style>' + css + '</style><body>' + body + '</body></html>')
    page.wait_for_function('Array.from(document.images).every(i=>i.complete&&i.naturalWidth>0)')
    page.evaluate('document.fonts.ready')


def main():
    if not (ROOT / 'dist/index.html').is_file():
        raise SystemExit('Build the frontend first: npm run build')
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=str(ROOT / 'dist')))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    origin = f'http://127.0.0.1:{server.server_address[1]}'
    report = dict(enabled=True, nextRefreshAt=iso(600), state=dict(status='idle', message='等待下一次刷新', accounts=[]))
    config = dict(cpampUrl='', managementConfigured=False, readToken='readme-example-only', bridgeUrl=origin,
                  city=dict(id='example-city', name='苏州 · 江苏'), sensors={}, quotaRefreshEnabled=True, quotaRefreshMinutes=10,
                  quotaRefresh=report, accounts=[], aiSources=dict(source='local'), appearance=THEMES[0][1],
                  clock=CLOCK, weather=dict(provider='open-meteo', apiKeyConfigured=False),
                  diagnostics=dict(lastClient=None, lastSnapshotRead=None, cacheFaults=[], desktopInput=None))
    errors, blocked = [], []
    try:
        with sync_playwright() as p:
            browser = p.chromium.launch(headless=True, executable_path=os.environ.get('WALLPAPER_TEST_CHROME', r'C:\Program Files\Google\Chrome\Application\chrome.exe'))
            context = browser.new_context(viewport=dict(width=800, height=1280), device_scale_factor=1)
            # Only this ephemeral local server can be contacted. All API calls are mocked.
            def route_request(route):
                url = route.request.url
                if '/v1/' in url:
                    path = url.split('/v1/', 1)[1]
                    if path == 'snapshot': route.fulfill(json=SNAPSHOT)
                    elif path == 'setup/session': route.fulfill(json=dict(session='readme-anonymous-session'))
                    elif path == 'setup/config/read': route.fulfill(json=config)
                    elif path == 'setup/quota-refresh/status': route.fulfill(json=report)
                    elif path == 'client': route.fulfill(status=204)
                    else: route.fulfill(json=dict(available=True, allowed=True, canRefresh=False, refresh=report))
                elif url.startswith(origin + '/') or url.startswith('data:') or url == 'about:blank': route.continue_()
                else:
                    blocked.append(url)
                    route.abort()
            context.route('**/*', route_request)
            page = context.new_page()
            page.on('pageerror', lambda error: errors.append(str(error)))
            page.clock.set_fixed_time(STAMP)
            images = []
            for title, theme in THEMES:
                saved = dict(version=2, readToken='readme-example-only', bridgeUrl=origin, materialMode='liquid',
                             theme=theme, motion=False, rotateSeconds=0, resolution='viewport',
                             material={group: dict(transparency=83, blur=26, edge=68) for group in ['top', 'quota', 'hardware']})
                page.goto(origin, wait_until='networkidle')
                page.evaluate('(v)=>localStorage.setItem("wallpaper.dashboard.v1",JSON.stringify(v))', saved)
                page.reload(wait_until='networkidle')
                page.wait_for_selector('.quota-card')
                page.wait_for_function('document.querySelector(".art").complete&&document.querySelector(".art").naturalWidth>0')
                assert page.locator('.quota-card').count() == 3
                assert page.locator('.wallpaper').get_attribute('data-theme') == theme['preset']
                images.append(data_image(page.screenshot()))
                if title == 'Glasswp-Purple':
                    details = []
                    for kind in ['clock', 'weather']:
                        page.locator('.' + kind).click()
                        dialog = page.get_by_role('dialog')
                        dialog.wait_for()
                        page.wait_for_timeout(350)
                        assert dialog.locator('.card-details-body').evaluate('n=>n.scrollHeight<=n.clientHeight+1')
                        details.append(data_image(dialog.screenshot()))
                        page.keyboard.press('Escape')
                        dialog.wait_for(state='hidden')
            compose = context.new_page()
            compose.set_viewport_size(dict(width=1260, height=812))
            common = '''*{box-sizing:border-box}body{margin:0;font-family:"Segoe UI","Microsoft YaHei UI",sans-serif;color:#f5f0ff;background:#17141f}img{display:block} .eyebrow{font-size:12px;letter-spacing:2px;color:#c0accf;text-transform:uppercase}.label{font-size:18px;font-weight:600}.hint{font-size:13px;color:#b7aabd}'''
            composition(compose, common + '''body{padding:32px 48px;background:radial-gradient(ellipse at 15% 0%,#3f2d51 0,transparent 55%),radial-gradient(ellipse at 90% 90%,#194a51 0,transparent 50%),#17141f}.head{display:flex;justify-content:space-between;align-items:baseline;margin-bottom:24px}.head b{font-size:24px;font-weight:500}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:28px}.screen{width:100%;border-radius:16px;border:1px solid #ffffff24;box-shadow:0 14px 30px #0005}.label{margin-top:17px}.hint{margin-top:4px}.footer{margin-top:24px;color:#b7aabd;font-size:12px}''',
                '<div class="head"><b>一块竖屏，三种氛围。</b><span class="eyebrow">GLASSWP / THEME COLLECTION</span></div><div class="grid">' +
                ''.join(f'<div><img class="screen" src="{image}"><div class="label">{title}</div><div class="hint">{subtitle}</div></div>' for (title, _), image, subtitle in zip(THEMES, images, ['紫色玻璃 · 柔和光影', '黄绿玻璃 · 霓虹氛围', '青蓝玻璃 · 清爽浅色'])) + '</div><div class="footer">实际界面截图 · 天气、额度与硬件读数均为匿名示例</div>')
            encode(compose, 'hero.webp')
            compose.set_viewport_size(dict(width=1260, height=900))
            composition(compose, common + '''body{padding:36px 42px;background:radial-gradient(ellipse at 30% 25%,#443154,transparent 65%),#17141f}.head{display:flex;justify-content:space-between;align-items:baseline;margin-bottom:24px}.head b{font-size:24px;font-weight:500}.layout{display:grid;grid-template-columns:440px 1fr;gap:36px;align-items:center}.screen{height:730px;border-radius:18px;box-shadow:0 12px 32px #0005}.right{display:flex;flex-direction:column;gap:24px}.detail{width:100%;border-radius:20px;border:1px solid #ffffff22;box-shadow:0 12px 32px #0005}.caption{margin:12px 3px 0;font-size:15px;color:#ddd1e7}.foot{margin-top:20px;font-size:12px;color:#b7aabd}''',
                '<div class="head"><b>日常信息，轻触展开。</b><span class="eyebrow">CLOCK / WEATHER / AI / HARDWARE</span></div>' +
                f'<div class="layout"><img class="screen" src="{images[0]}"><div class="right"><div><img class="detail" src="{details[0]}"><p class="caption">世界时钟：在同一视图查看多个时区</p></div><div><img class="detail" src="{details[1]}"><p class="caption">天气详情：未来 12 小时温度与降水趋势</p></div></div></div><div class="foot">实际界面截图 · 所有读数仅用于演示</div>')
            encode(compose, 'dashboard.webp')
            page.set_viewport_size(dict(width=1440, height=1080))
            page.goto(origin + '/admin/', wait_until='networkidle')
            page.wait_for_function('document.querySelector("#appearance-status").textContent === "主题设置已读取。"')
            page.wait_for_function('document.querySelector("#bridge-state").dataset.state === "ok"')
            # Top viewport includes only the original theme/time settings and sidebar.
            assert not page.locator('#read-token').is_visible() or page.locator('#read-token').bounding_box()['y'] > 1080
            encode(page, 'settings.webp')
            assert not errors, errors
            assert not blocked, blocked
            browser.close()
    finally:
        server.shutdown()
        server.server_close()
    summary = dict(anonymousFixtures=True, liveApiRequests=0, browserErrors=errors,
                   images={path.name: path.stat().st_size for path in OUT.glob('*.webp')})
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == '__main__':
    main()
