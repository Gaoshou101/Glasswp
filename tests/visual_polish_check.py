"""Anonymous production-browser checks for visual semantics and motion fallbacks."""
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
OUT = ROOT / 'artifacts/validation/visual-polish'
OUT.mkdir(parents=True, exist_ok=True)
iso = lambda offset=0: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time() + offset))
metric = lambda value, unit='%': dict(value=value, unit=unit, source='example', sensorId=None)
device = lambda name, value: dict(name=name, load=metric(value), temperature=metric(45, '°C'), usedBytes=None, totalBytes=None)

def window(ident, remaining):
    return dict(id=ident, label=ident, remainingPercent=remaining, usedValue=None, limitValue=None, quotaUnit='',
                resetAt=iso(7200), durationSeconds=18000, mode='fixed', accuracy='exact', observedAt=iso(), stale=False)

snapshot = dict(schemaVersion=1, generatedAt=iso(),
    hardware=dict(status='ok', observedAt=iso(), message='Example', **{key:device(key, 20) for key in ['cpu', 'gpu', 'vram', 'ram']}),
    weather=dict(status='ok', fetchedAt=iso(), observedAt=iso(), city='Example', temperature=23, low=19, high=26, code=0, isDay=True, message=''),
    quota=dict(status='ok', fetchedAt=iso(), message='', accounts=[dict(id=provider, alias=provider, provider=provider, plan='Example',
        windows=[window('Known empty', 0), window('Unknown', None)]) for provider in ['codex', 'claude', 'antigravity']]))
errors, results = [], {}

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport=dict(width=1600, height=2560))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps(dict(
        version=2, readToken='anonymous', rotateSeconds=0, quotaLayout='cards', materialMode='liquid', trends=True))) + ');')
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
    apply = lambda props: page.evaluate('props => window.wallpaperPropertyListener.applyUserProperties(props)', {key:dict(value=value) for key, value in props.items()})

    tracks = page.locator('.quota-card').first.locator('.track').evaluate_all('nodes => nodes.map(n => ({unknown:n.dataset.unknown, background:getComputedStyle(n).backgroundImage}))')
    assert tracks[0]['unknown'] == 'false' and tracks[0]['background'] == 'none', tracks
    assert tracks[1]['unknown'] == 'true' and 'repeating-linear-gradient' in tracks[1]['background'], tracks
    results['unknown_quota_retains_stripes_distinct_from_empty'] = True

    provider_colors = dict(zip(page.locator('.account-mark').evaluate_all('nodes=>nodes.map(n=>n.dataset.provider)'),
                               page.locator('.account-mark').evaluate_all('nodes=>nodes.map(n=>getComputedStyle(n).color)')))
    assert len(set(provider_colors.values())) == 3, provider_colors
    logos = page.locator('.account-mark img')
    page.wait_for_function('Array.from(document.querySelectorAll(".account-mark img")).every(n=>n.complete&&n.naturalWidth>0)')
    sizes = logos.evaluate_all('nodes=>nodes.map(n=>({width:n.getBoundingClientRect().width,height:n.getBoundingClientRect().height,fit:getComputedStyle(n).objectFit,scheme:getComputedStyle(n).colorScheme}))')
    assert len(sizes) == 3 and all(size == sizes[0] for size in sizes), sizes
    assert sizes[0]['width'] == sizes[0]['height'] and sizes[0]['fit'] == 'contain' and sizes[0]['scheme'] == 'dark', sizes
    apply(dict(quota_layout='carousel'))
    for provider in ['codex', 'claude', 'antigravity']:
        mark = page.locator('.account-mark')
        page.wait_for_function('provider=>document.querySelector(".account-mark").dataset.provider===provider', arg=provider)
        assert mark.get_attribute('data-provider') == provider, (provider, mark.get_attribute('data-provider'))
        assert mark.evaluate('n=>getComputedStyle(n).color') == provider_colors[provider]
        assert mark.locator('img').get_attribute('aria-hidden') == 'true'
        page.wait_for_function('document.querySelector(".account-mark img").complete&&document.querySelector(".account-mark img").naturalWidth>0')
        if provider != 'antigravity':
            page.get_by_role('button', name='下一页额度').click()
    results['provider_tints_survive_both_layouts'] = provider_colors
    results['official_logos_load_at_uniform_size'] = sizes[0]

    hardware_colors = page.locator('.hardware-icon').evaluate_all('nodes=>nodes.map(n=>getComputedStyle(n).color)')
    assert len(set(hardware_colors)) == 4, hardware_colors
    results['hardware_devices_keep_distinct_tints'] = hardware_colors
    values = page.locator('.hardware-value').evaluate_all('nodes=>nodes.map(n=>({text:n.textContent,number:n.querySelector(".value-number").textContent,unit:n.querySelector(".value-unit").textContent}))')
    assert all(value == dict(text='20%', number='20', unit='%') for value in values), values
    results['hardware_unit_split_preserves_readable_value'] = True

    weather_revision = 0
    for code, day, kind, shape in [(0, False, 'clear', 'wi-moon'), (2, True, 'partly', 'wi-sun'), (2, False, 'partly', 'wi-moon'),
                                   (63, True, 'rain', 'wi-drop'), (95, True, 'thunder', 'wi-bolt'), (None, True, 'unknown', 'wi-cloud-muted')]:
        weather_revision += 1
        snapshot['weather'].update(code=code, isDay=day, fetchedAt=iso(weather_revision), observedAt=iso(weather_revision))
        apply({})
        page.wait_for_function('shape=>!!document.querySelector(".weather-icon ."+shape)', arg=shape)
        assert page.locator('.weather-icon').get_attribute('data-kind') == kind
    results['weather_changes_draw_day_night_and_condition_icons'] = True

    # Separate line and area segments must leave a real gap over a missing sample.
    # Begin a fresh sample sequence after a deliberate timestamp discontinuity.
    sample_base = time.time() + 60
    for index, value in enumerate([21, 30, None, 40, 50]):
        observed = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(sample_base + index))
        snapshot['hardware']['observedAt'] = observed
        snapshot['hardware']['cpu']['load']['value'] = value
        apply({})
        page.wait_for_function('value=>document.querySelector("[data-device=cpu] .hardware-value").textContent===value', arg='—' if value is None else f'{value}%')
    segments = page.locator('[data-device=cpu] .spark-lines polyline').evaluate_all('nodes=>nodes.map(n=>n.getAttribute("points"))')
    assert len(segments) == 2, segments
    left, right = [[float(point.split(',')[0]) for point in segment.split()] for segment in segments]
    assert max(left) < min(right), segments
    assert page.locator('[data-device=cpu] .spark-areas path').count() == 2
    results['trend_lines_and_fills_preserve_missing_sample_gap'] = True

    clock_checks = []
    for width, height in [(800, 1280), (1080, 1920), (1200, 1600), (1600, 2560)]:
        page.set_viewport_size(dict(width=width, height=height))
        for seconds, hour12 in [(False, False), (True, False), (False, True), (True, True)]:
            apply(dict(seconds=seconds, hour12=hour12))
            fits = page.locator('.time').evaluate('''n=>{const range=document.createRange();range.selectNodeContents(n);const t=range.getBoundingClientRect(), b=n.closest('.clock').getBoundingClientRect();return t.left>=b.left-1&&t.right<=b.right+1&&t.top>=b.top-1&&t.bottom<=b.bottom+1}''')
            assert fits, (width, height, seconds, hour12, page.locator('.time').inner_text())
            clock_checks.append(dict(width=width, height=height, seconds=seconds, hour12=hour12, fits=True))
    results['clock_variants_fit_portrait_sizes'] = clock_checks

    transition_off = lambda: page.locator('.liquid-glint').first.evaluate('n=>getComputedStyle(n,"::after").transitionDuration.split(",").every(value=>parseFloat(value)===0)')
    apply(dict(motion=False))
    assert transition_off(), 'Disabled motion must remove the rim transition'
    apply(dict(motion=True))
    page.evaluate('window.wallpaperPropertyListener.setPaused(true)')
    assert transition_off(), 'Paused wallpaper must remove the rim transition'
    page.evaluate('window.wallpaperPropertyListener.setPaused(false)')
    page.emulate_media(reduced_motion='reduce')
    page.wait_for_function('document.querySelector(".wallpaper").dataset.dynamic==="false"')
    assert transition_off(), 'Reduced motion must remove the rim transition'
    results['rim_transition_respects_disabled_paused_and_reduced_motion'] = True

    assert not errors, errors
    results['browser_errors'] = errors
    browser.close()

(OUT / 'result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(results, ensure_ascii=False))
