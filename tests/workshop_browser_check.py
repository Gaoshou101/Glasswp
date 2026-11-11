"""Anonymous file-URL acceptance of the three fixed-theme Workshop packages.

All bridge traffic is intercepted; no real service or account is accessed.
"""
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
OUT = ROOT / 'artifacts/validation/workshop-packages'
OUT.mkdir(parents=True, exist_ok=True)
EXPECTED = {
    'Glasswp-Purple': dict(preset='original', background='assets/wallpaper-original.jpg', accent='#dcc6ff', surface='dark'),
    'Glasswp-Angel': dict(preset='neon', background='assets/wallpapers/neon.jpg', accent='#e5f34d', surface='dark'),
    'Glasswp-Corgi': dict(preset='pool', background='assets/wallpapers/pool.jpg', accent='#147f91', surface='light'),
}


def snapshot(appearance):
    now = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    metric = dict(value=25, unit='%', source='example', sensorId=None)
    device = dict(name='Example device', load=metric,
                  temperature=dict(value=40, unit='°C', source='example', sensorId=None),
                  usedBytes=None, totalBytes=None)
    def quota_window(ident, remaining):
        return dict(id=ident, label=ident, remainingPercent=remaining, usedValue=None, limitValue=None,
                    quotaUnit='', resetAt=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time() + 7200)),
                    durationSeconds=18000, mode='fixed', accuracy='exact', observedAt=now, stale=False)
    accounts = [dict(id=provider, alias='Example ' + provider, provider=provider, plan='Example',
                     windows=[quota_window('Known empty', 0), quota_window('Unknown', None)])
                for provider in ['codex', 'claude', 'antigravity']]
    hourly = [dict(time=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time() + hour * 3600)),
                   temperature=23 - hour * .4, precipitationProbability=None if hour == 3 else 0 if hour < 6 else 65,
                   precipitationMm=None if hour == 3 else 0 if hour < 6 else 1.2, code=2 if hour < 6 else 61)
              for hour in range(12)]
    return dict(schemaVersion=1, generatedAt=now, appearance=appearance,
                clock=dict(timezone='Asia/Shanghai', offsetSeconds=0, seconds=False, hour12=False,
                           worldZones=['Asia/Shanghai', 'Asia/Tokyo', 'Asia/Singapore', 'Europe/London', 'America/New_York', 'America/Los_Angeles']),
                revisions=dict(epoch='anonymous-workshop-test', hardware=1, quota=1, weather=1),
                hardware=dict(status='ok', observedAt=now, cpu=device, gpu=device, vram=device, ram=device, message=''),
                quota=dict(status='ok', fetchedAt=now, accounts=accounts, message=''),
                weather=dict(status='ok', fetchedAt=now, observedAt=now, city='示例城市', temperature=23,
                             low=19, high=26, code=2, isDay=True, timezone='Asia/Shanghai', message='', hourly=hourly))


def component_semantics(page):
    page.wait_for_selector('.quota-card')
    tracks = page.locator('.quota-card').first.locator('.track').evaluate_all('''nodes => nodes.map(n => ({
        unknown:n.dataset.unknown, background:getComputedStyle(n).backgroundImage,
        fillTransform: n.firstElementChild.style.transform
    }))''')
    assert tracks[0] == dict(unknown='false', background='none', fillTransform='scaleX(0)'), tracks
    assert tracks[1]['unknown'] == 'true' and 'repeating-linear-gradient' in tracks[1]['background'], tracks
    hardware_colors = page.locator('.hardware-icon').evaluate_all('nodes=>nodes.map(n=>getComputedStyle(n).color)')
    assert len(hardware_colors) == len(set(hardware_colors)) == 4, hardware_colors
    page.wait_for_function('Array.from(document.querySelectorAll(".account-mark img")).every(n=>n.complete&&n.naturalWidth>0)')
    return dict(zeroAndUnknownQuotaDistinct=True, hardwareColors=hardware_colors,
                anonymousAccountCount=page.locator('.quota-card').count())


def corgi_details(page):
    # Verify computed colors, not only the theme's data attribute.
    details = {}
    for kind, card, text_selector in [
        ('clock', '.clock', '.world-clock-time,.world-clock-name'),
        ('weather', '.weather', '.weather-current-temperature,.weather-current-city,.weather-current-condition,.forecast-summary'),
        ('hardware', '[data-device=cpu]', '.insight-stats dd,.chart-readout,.insight-note'),
    ]:
        page.locator(card).click()
        dialog = page.get_by_role('dialog')
        dialog.wait_for()
        colors = dialog.locator(text_selector).evaluate_all('nodes => nodes.map(n=>({text:n.textContent,color:getComputedStyle(n).color}))')
        assert colors, (kind, 'Expected detail labels are missing')
        for entry in colors:
            channels = [float(value.strip()) for value in entry['color'].split('(')[1].split(')')[0].split(',')[:3]]
            assert max(channels) < 180, (kind, entry)
        panel = dialog.evaluate('n=>({background:getComputedStyle(n).backgroundColor,color:getComputedStyle(n).color})')
        panel_channels = [float(value.strip()) for value in panel['background'].split('(')[1].split(')')[0].split(',')[:3]]
        assert min(panel_channels) >= 240, (kind, panel)
        if kind == 'weather':
            assert dialog.locator('.forecast-probability').nth(0).inner_text() == '0%'
            assert dialog.locator('.forecast-probability').nth(3).inner_text() == '—'
            assert 'repeating-linear-gradient' in dialog.locator('.forecast-rain-track').nth(3).evaluate('n=>getComputedStyle(n).backgroundImage')
        page.wait_for_timeout(250)
        dialog.screenshot(path=str(OUT / f'Glasswp-Corgi-{kind}-detail.png'))
        details[kind] = dict(panel=panel, textColors=colors)
        page.keyboard.press('Escape')
        dialog.wait_for(state='hidden')
    return details


def assert_theme(page, title, expected):
    page.wait_for_function('document.querySelector(".art").complete && document.querySelector(".art").naturalWidth > 0')
    actual = page.locator('.wallpaper').evaluate('''root => ({
        preset: root.dataset.theme, surface: root.dataset.surface,
        accent: getComputedStyle(root).getPropertyValue('--accent').trim(),
        background: root.querySelector('.art').currentSrc,
        packagePreset: document.documentElement.dataset.wallpaperPreset
    })''')
    assert actual['preset'] == expected['preset'], (title, actual)
    assert actual['packagePreset'] == expected['preset'], (title, actual)
    assert actual['surface'] == expected['surface'], (title, actual)
    assert actual['accent'] == expected['accent'], (title, actual)
    assert actual['background'] == (ROOT / 'artifacts' / title / expected['background']).as_uri(), (title, actual)
    return actual


results = dict(packages={}, browserErrors=[])
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    for title, expected in EXPECTED.items():
        # The intentionally conflicting palette includes a custom color and surface.
        other = EXPECTED['Glasswp-Corgi' if expected['preset'] != 'pool' else 'Glasswp-Angel']
        conflict = dict(other, accent='#ff0000', surface='light' if expected['surface'] == 'dark' else 'dark')
        checks = {}
        for mode in ['no-ticket', 'conflicting-bridge']:
            context = browser.new_context(viewport=dict(width=900, height=1600))
            saved = dict(version=2, readToken='' if mode == 'no-ticket' else 'anonymous-workshop-test',
                         bridgeUrl=BASE, materialMode='liquid', theme=conflict)
            context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps(saved)) + ');')
            page = context.new_page()
            page.set_default_timeout(15000)
            errors, requests = [], []
            page.on('pageerror', lambda error: errors.append(str(error)))

            def respond(route):
                requests.append(route.request.url)
                if route.request.url.endswith('/v1/snapshot'):
                    route.fulfill(json=snapshot(conflict))
                elif route.request.url.endswith('/v1/client'):
                    route.fulfill(status=204)
                else:
                    route.fulfill(json=dict(canRefresh=False, available=True, allowed=True,
                                            refresh=dict(state=dict(status='idle', message=''))))

            page.route(BASE + '/**', respond)
            page.goto((ROOT / 'artifacts' / title / 'index.html').as_uri(), wait_until='networkidle')
            initial = assert_theme(page, title, expected)
            if mode == 'conflicting-bridge':
                page.wait_for_function('document.getElementById("connection-status").textContent === "本机数据已连接。"')
                assert any(url.endswith('/v1/snapshot') for url in requests), (title, requests)
                assert_theme(page, title, expected)
                semantics = component_semantics(page)
                # Force another snapshot to exercise the Wallpaper Engine lifecycle.
                page.evaluate('window.wallpaperPropertyListener.applyUserProperties({})')
                page.wait_for_timeout(150)
                assert_theme(page, title, expected)
            else:
                assert '请在设置中连接本机数据' in page.locator('.note').inner_text()
                assert not requests, (title, 'No-ticket package unexpectedly fetched bridge data', requests)
            page.reload(wait_until='networkidle')
            if mode == 'conflicting-bridge':
                page.wait_for_function('document.getElementById("connection-status").textContent === "本机数据已连接。"')
            after_reload = assert_theme(page, title, expected)
            assert initial == after_reload
            page.screenshot(path=str(OUT / f'{title}-{mode}.png'))
            assert not errors, (title, mode, errors)
            checks[mode] = dict(theme=after_reload, bridgeRequests=len(requests), reloadPassed=True, browserErrors=errors)
            if mode == 'conflicting-bridge':
                checks[mode]['components'] = semantics
                if expected['preset'] == 'pool':
                    checks[mode]['lightDetails'] = corgi_details(page)
                    assert not errors, (title, mode, errors)
            context.close()
        results['packages'][title] = checks
    browser.close()

(OUT / 'results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(results, ensure_ascii=False, indent=2))
