"""Anonymous browser acceptance for managed clocks and guarded world-clock details."""
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
OUT = ROOT / 'artifacts/validation/world-clock'
OUT.mkdir(parents=True, exist_ok=True)
iso = lambda: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
metric = dict(value=25, unit='%', source='example', sensorId=None)
device = dict(name='Example', load=metric, temperature=dict(value=40, unit='°C', source='example', sensorId=None), usedBytes=None, totalBytes=None)
clock = dict(timezone='Asia/Shanghai', offsetSeconds=0, seconds=True, hour12=False,
             worldZones=['Asia/Shanghai', 'Asia/Tokyo', 'Asia/Singapore', 'Europe/London', 'America/New_York', 'America/Los_Angeles'])
snapshot = dict(schemaVersion=1, generatedAt=iso(), clock=clock,
                revisions=dict(epoch='clock-test', hardware=1, quota=1, weather=1),
                hardware=dict(status='ok', observedAt=iso(), cpu=device, gpu=device, vram=device, ram=device, message=''),
                quota=dict(status='ok', fetchedAt=iso(), accounts=[], message=''),
                weather=dict(status='ok', fetchedAt=iso(), observedAt=iso(), city='示例城市', temperature=23, low=19, high=26, code=2, isDay=True, timezone='Asia/Shanghai', message='', hourly=[]))
state = dict(allowed=True)
errors = []
results = {}

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport=dict(width=1600, height=2560))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps(dict(version=2, readToken='anonymous-clock-test', materialMode='liquid'))) + ');')
    page = context.new_page()
    page.on('pageerror', lambda error: errors.append(str(error)))

    def respond(route):
        if route.request.url.endswith('/v1/snapshot'):
            route.fulfill(json=snapshot)
        elif route.request.url.endswith('/v1/desktop-input'):
            route.fulfill(json=dict(available=True, allowed=state['allowed']))
        else:
            route.fulfill(json=dict(canRefresh=False, refresh=dict(state=dict(status='idle', message=''))))

    page.route(BASE + '/**', respond)
    page.goto((ROOT / 'dist/index.html').as_uri(), wait_until='networkidle')
    page.wait_for_function('document.querySelector(".time").dataset.seconds === "true"')
    assert page.locator('#timezone').is_disabled()
    assert '管理页' in page.locator('#clock-config-status').inner_text()
    page.locator('.clock').click()
    dialog = page.get_by_role('dialog', name='世界时钟')
    dialog.wait_for()
    assert dialog.locator('.world-clock-row').count() == 6
    assert dialog.locator('[data-primary=true]').get_attribute('data-timezone') == 'Asia/Shanghai'
    assert dialog.locator('.world-clock-offset').first.inner_text() == 'UTC+08:00'
    assert dialog.get_by_role('link', name='时间设置').get_attribute('href').endswith('/admin/#clock-heading')
    before = dialog.locator('.world-clock-time').first.inner_text()
    page.wait_for_function('before => document.querySelector(".world-clock-time").textContent !== before', arg=before)
    overflow = dialog.evaluate('panel => ({panel:panel.scrollHeight > panel.clientHeight + 1, body:panel.querySelector(".card-details-body").scrollHeight > panel.querySelector(".card-details-body").clientHeight + 1, horizontal:[...panel.querySelectorAll(".world-clock-row")].some(n=>n.scrollWidth>n.clientWidth+1)})')
    page.screenshot(path=str(OUT / 'world-clock.png'))
    # The decorative optics intentionally exceed the panel box; real content must fit.
    assert not overflow['body'] and not overflow['horizontal'], overflow
    results['world_clock_live_rows_and_layout'] = overflow
    page.keyboard.press('Escape')
    dialog.wait_for(state='hidden')
    page.locator('.clock').focus()
    page.keyboard.press('Enter')
    dialog.wait_for()
    page.keyboard.press('Escape')
    dialog.wait_for(state='hidden')
    assert page.locator('.clock').evaluate('n=>n===document.activeElement')
    results['keyboard_open_escape_restore'] = True

    # Config changes must apply even when all source revisions are unchanged.
    clock.update(timezone='Asia/Kathmandu', offsetSeconds=3600, seconds=False, hour12=True, worldZones=[])
    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({})')
    page.wait_for_function('document.querySelector("#timezone").value === "Asia/Kathmandu"')
    assert page.locator('.time').get_attribute('data-seconds') == 'false'
    assert page.locator('.time').get_attribute('data-hour12') == 'true'
    assert page.locator('.time').inner_text() == page.evaluate('new Intl.DateTimeFormat("en-GB",{timeZone:"Asia/Kathmandu",hour:"2-digit",minute:"2-digit",hour12:true}).format(new Date(Date.now()+3600000))')
    page.route(BASE + '/v1/snapshot', lambda route: route.abort())
    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({timezone:{value:"UTC"},seconds:{value:true},hour12:{value:false}})')
    assert page.locator('#timezone').input_value() == 'Asia/Kathmandu'
    assert page.locator('.time').get_attribute('data-seconds') == 'false'
    assert page.locator('.time').get_attribute('data-hour12') == 'true'
    page.unroute(BASE + '/v1/snapshot')
    page.locator('.clock').click()
    dialog.wait_for()
    assert dialog.locator('.world-clock-row').count() == 1
    assert dialog.locator('.world-clock-offset').inner_text() == 'UTC+05:45'
    assert '+3600 秒' in dialog.locator('[data-primary=true]').get_attribute('title')
    page.keyboard.press('Escape')
    dialog.wait_for(state='hidden')
    results['managed_revision_independent_calibration_and_empty_world_list'] = True

    # WE forwards mouseup: the time card uses the same native authorization as all details.
    state['allowed'] = False
    box = page.locator('.clock').bounding_box()
    page.mouse.move(box['x']+box['width']/2, box['y']+box['height']/2)
    page.mouse.up()
    page.wait_for_timeout(150)
    assert page.get_by_role('dialog').count() == 0
    state['allowed'] = True
    page.mouse.up()
    dialog.wait_for()
    assert dialog.locator('.world-clock-row').count() == 1
    page.keyboard.press('Escape')
    dialog.wait_for(state='hidden')
    results['wallpaper_mouseup_gate'] = True

    clock['worldZones'] = ['UTC', 'Asia/Shanghai', 'Asia/Tokyo', 'Asia/Singapore', 'Asia/Hong_Kong', 'Asia/Seoul', 'Asia/Kolkata', 'Asia/Kathmandu', 'Asia/Dubai', 'Europe/London', 'Europe/Paris', 'Europe/Berlin', 'Europe/Rome', 'Europe/Moscow', 'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles', 'America/Toronto', 'America/Vancouver', 'America/Sao_Paulo', 'Australia/Sydney', 'Australia/Perth', 'Pacific/Auckland']
    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({})')
    page.wait_for_function('JSON.parse(localStorage.getItem("wallpaper.dashboard.v1")).worldZones.length === 24')
    page.locator('.clock').click()
    dialog.wait_for()
    assert dialog.locator('.world-clock-row').count() == 24
    body = dialog.locator('.card-details-body')
    assert body.evaluate('n=>n.scrollHeight>n.clientHeight')
    body.evaluate('n=>n.scrollTop=n.scrollHeight')
    assert body.evaluate('n=>n.scrollTop>0')
    page.keyboard.press('Escape')
    dialog.wait_for(state='hidden')
    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({bridge_url:{value:"http://127.0.0.1:17633"},timezone:{value:"UTC"},seconds:{value:true},hour12:{value:false}})')
    assert not page.locator('#timezone').is_disabled()
    assert page.locator('#timezone').input_value() == 'UTC'
    results['many_world_zones_scroll_and_connection_change_unlocks'] = True
    assert not errors, errors
    results['browser_errors'] = errors
    browser.close()

(OUT / 'results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(results, ensure_ascii=False, indent=2))
