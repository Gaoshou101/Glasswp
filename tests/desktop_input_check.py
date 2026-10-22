"""Replay WE's release-only desktop input against the production bundle."""
import json
import sys
import time
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
iso = lambda offset=0: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time() + offset))
metric = lambda value: dict(value=value, unit='%', source='example', sensorId=None)
device = dict(name='Example', load=metric(20), temperature=metric(40), usedBytes=None, totalBytes=None)
account = dict(id='example', alias='示例账号', provider='codex', plan='Pro', windows=[], resetCredits=dict(available=2, observedAt=iso(), stale=False))
snapshot = dict(schemaVersion=1, generatedAt=iso(),
    quota=dict(status='ok', fetchedAt=iso(), accounts=[account], message=''),
    hardware=dict(status='ok', observedAt=iso(), cpu=device, gpu=device, vram=device, ram=device, message=''),
    weather=dict(status='ok', fetchedAt=iso(), observedAt=iso(), city='示例城市', temperature=23, low=19, high=26, code=2, isDay=True, timezone='Asia/Shanghai', message='', hourly=[dict(time=iso(i*3600), temperature=23, precipitationProbability=0, precipitationMm=0, code=2) for i in range(24)]))
queries, errors, results = [], [], {}
input_state = dict(available=True, allowed=True, delay=0)

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport=dict(width=1600, height=2560))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps(dict(version=1, readToken='preview-example', rotateSeconds=0, materialMode='liquid'))) + ');')
    page = context.new_page()
    page.on('pageerror', lambda error: errors.append(str(error)))

    def respond(route):
        path = urlsplit(route.request.url).path
        if path == '/v1/desktop-input':
            if input_state['delay']:
                time.sleep(input_state['delay'])
            route.fulfill(json=dict(available=input_state['available'], allowed=input_state['allowed']))
        elif path == '/v1/snapshot':
            route.fulfill(json=snapshot)
        elif path.endswith('/activity'):
            query = parse_qs(urlsplit(route.request.url).query)
            queries.append(query)
            older = 'beforeId' in query
            route.fulfill(json=dict(accountId='example', status='ok', fetchedAt=iso(), **{'from':iso(-7*86400)}, to=iso(), requests=25, successRate=100, tokens=1000, estimatedCost=0, lastActivity=iso(), recent=[dict(time=iso(-i*60), model='example', success=True, statusCode=200, tokens=10, inputTokens=8, outputTokens=2, cachedTokens=0, durationMs=100, ttftMs=50) for i in range(5 if older else 20)], hasMore=not older, nextBeforeMs=int(time.time()*1000)-1200000, nextBeforeId=42, totalCount=25, message=''))
        else:
            route.fulfill(json=dict(canRefresh=True, snapshotFetchedAt=iso(), refresh=dict(state=dict(status='idle', message='', lastFinishedAt=None))))

    page.route(BASE + '/**', respond)
    page.goto((ROOT / 'dist/index.html').as_uri(), wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({})')

    input_state['allowed'] = False
    box = page.locator('.quota-card').bounding_box()
    page.mouse.move(box['x'] + box['width']/2, box['y'] + box['height']/2)
    page.mouse.up()
    page.wait_for_timeout(100)
    assert page.get_by_role('dialog').count() == 0, 'Covered release must not open a card'
    page.mouse.down()
    assert page.locator('.liquid-pressed').count() == 0, 'Covered press must not paint a click effect'
    page.mouse.up()
    page.wait_for_timeout(100)
    assert page.get_by_role('dialog').count() == 0, 'Covered native click must not open a card'
    results['covered_release_native_click_and_press_blocked'] = True
    input_state.update(allowed=True, available=False)
    page.mouse.up()
    page.wait_for_timeout(100)
    assert page.get_by_role('dialog').count() == 0, 'Unavailable guard must fail closed'
    input_state['available'] = True

    input_state['delay'] = .08
    page.evaluate('setTimeout(() => window.wallpaperPropertyListener.setPaused(true), 10)')
    page.mouse.up()
    page.wait_for_timeout(200)
    assert page.get_by_role('dialog').count() == 0, 'An approval completed after pause must not activate a card'
    input_state['delay'] = 0
    page.evaluate('window.wallpaperPropertyListener.setPaused(false)')
    page.route(BASE + '/v1/desktop-input', lambda route: route.abort())
    page.mouse.up()
    page.wait_for_timeout(100)
    assert page.get_by_role('dialog').count() == 0, 'A disconnected bridge must fail closed'
    page.unroute(BASE + '/v1/desktop-input')
    results['unavailable_disconnected_and_pause_pending_fail_closed'] = True

    def release(locator, button='left'):
        box = locator.bounding_box()
        assert box
        page.mouse.move(box['x'] + box['width']/2, box['y'] + box['height']/2)
        page.mouse.up(button=button)
        page.wait_for_timeout(100)

    release(page.locator('.quota-card'))
    assert page.get_by_role('dialog').count() == 1, 'WE release-only input must open the account card'
    page.wait_for_selector('.request-activity-table')
    assert queries[-1].get('range') == ['7d']
    results['release_only_opens_account_default_seven_days'] = True

    input_state['allowed'] = False
    release(page.get_by_role('button', name='关闭详情'))
    assert page.get_by_role('dialog').count() == 1, 'Covered click must not close an open dialog'
    page.mouse.move(20, 1200)
    page.mouse.up()
    page.wait_for_timeout(100)
    assert page.get_by_role('dialog').count() == 1, 'Covered background click must not dismiss a dialog'
    input_state['allowed'] = True
    results['covered_close_and_background_dismiss_blocked'] = True

    assert page.locator('.details-close svg path').count() == 1
    assert page.locator('.details-close').inner_text() == ''
    wrap = page.locator('.request-table-scroll')
    page.wait_for_timeout(400)
    size = wrap.evaluate('n=>({height:n.clientHeight,total:n.scrollHeight})')
    assert size['total'] > size['height'], size
    wrap.hover()
    page.mouse.wheel(0, 130)
    page.wait_for_function('document.querySelector(".request-table-scroll").scrollTop>0')
    results['native_wheel_scrolls_request_rows'] = True
    wrap.evaluate('n=>n.scrollTop=0')
    page.wait_for_timeout(100)
    release(page.get_by_role('button', name='向下滚动请求'))
    assert wrap.evaluate('n=>n.scrollTop') > 0
    release(page.get_by_role('button', name='向上滚动请求'))
    assert wrap.evaluate('n=>n.scrollTop') <= 1
    page.get_by_role('button', name='向下滚动请求').hover()
    page.wait_for_timeout(700)
    assert wrap.evaluate('n=>n.scrollTop') > 0
    page.mouse.move(20, 1200)
    stopped = wrap.evaluate('n=>n.scrollTop')
    page.wait_for_timeout(200)
    assert abs(wrap.evaluate('n=>n.scrollTop') - stopped) <= 1
    wrap.evaluate('n=>n.scrollTop=n.scrollHeight')
    page.wait_for_timeout(100)
    assert page.get_by_role('button', name='向下滚动请求').is_disabled()
    results['desktop_release_and_hover_scroll_stop_at_bounds'] = True
    for width, height in [(800,1280),(1080,1920),(1600,2560),(2160,3840)]:
        page.set_viewport_size(dict(width=width,height=height))
        page.wait_for_timeout(100)
        assert page.locator('.card-details-body').evaluate('n=>n.scrollHeight<=n.clientHeight+1 && n.scrollWidth<=n.clientWidth+1')
    page.set_viewport_size(dict(width=1600,height=2560))
    page.wait_for_timeout(100)
    page.get_by_role('dialog').screenshot(path=str(ROOT / 'artifacts/validation/request-scroll-close-1600.png'))
    results['close_icon_and_scroll_controls_fit_four_portrait_sizes'] = True

    release(page.get_by_role('combobox', name='请求时间范围'))
    assert page.get_by_role('listbox').is_visible()
    release(page.get_by_role('option', name='今天', exact=True))
    page.wait_for_function('document.querySelector(".activity-period").dataset.value==="today"')
    page.wait_for_function('document.querySelector(".activity-reload").disabled===false')
    assert queries[-1].get('range') == ['today']
    results['release_only_menu_and_range_selection'] = True

    page.wait_for_function('!document.querySelector("[data-activity-focus=next]").disabled')
    release(page.get_by_role('button', name='较早请求'))
    page.wait_for_function('document.querySelector(".activity-page-number").textContent.includes("2")')
    page.wait_for_function('!document.querySelector("[data-activity-focus=previous]").disabled')
    assert queries[-1].get('beforeId') == ['42']
    release(page.get_by_role('button', name='较新请求'))
    page.wait_for_function('!document.querySelector("[data-activity-focus=next]").disabled')
    before = len(queries)
    release(page.get_by_role('button', name='较新请求'))
    assert len(queries) == before
    results['release_only_paging_and_disabled_button'] = True

    release(page.get_by_role('button', name='关闭详情'))
    assert page.get_by_role('dialog').count() == 0
    release(page.locator('.weather'))
    assert page.get_by_role('dialog').get_attribute('data-kind') == 'weather'
    assert page.locator('.forecast-hour').count() == 12
    release(page.get_by_role('button', name='关闭详情'))
    release(page.locator('.hardware-item').first)
    assert page.get_by_role('dialog').get_attribute('data-kind') == 'hardware'
    release(page.get_by_role('button', name='关闭详情'))
    results['release_only_weather_hardware_and_close'] = True

    # Missing native down leaves CEF's keyboard focus heuristic active. A mouse
    # close must not paint a focus frame on the original GPU card afterward.
    page.keyboard.press('Tab')
    release(page.locator('.hardware-item').nth(1))
    release(page.get_by_role('button', name='关闭详情'))
    page.wait_for_timeout(250)
    gpu = page.locator('.hardware-item').nth(1)
    assert gpu.evaluate('n=>getComputedStyle(n).outlineStyle') == 'none', 'Mouse close left a keyboard focus frame on GPU'
    assert page.locator('.card-details').count() == 0
    gpu.screenshot(path=str(ROOT / 'artifacts/validation/gpu-after-pointer-close.png'))
    results['release_only_close_clears_card_focus_frame'] = True

    release(page.locator('.quota-card'), 'right')
    assert page.get_by_role('dialog').count() == 0
    page.mouse.move(20, 1200)
    page.mouse.up()
    assert page.get_by_role('dialog').count() == 0
    results['right_button_and_unrelated_release_ignored'] = True

    for _ in range(2):
        page.locator('.quota-card').click()
        page.wait_for_timeout(100)
        assert page.get_by_role('dialog').count() == 1
        page.get_by_role('button', name='关闭详情').click()
        page.wait_for_timeout(100)
        assert page.get_by_role('dialog').count() == 0
        assert page.locator('.quota-card').evaluate('n=>getComputedStyle(n).outlineStyle') == 'none'
    results['normal_down_up_click_runs_once_in_engine'] = True

    card = page.locator('.quota-card')
    box = card.bounding_box()
    page.mouse.move(box['x']+10, box['y']+10)
    page.mouse.down()
    page.mouse.move(20, 1200)
    page.mouse.up()
    assert page.get_by_role('dialog').count() == 0
    results['drag_release_does_not_activate_card'] = True

    page.reload(wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    release(page.locator('.quota-card'))
    assert page.get_by_role('dialog').count() == 0
    page.locator('.quota-card').click()
    assert page.get_by_role('dialog').count() == 1
    page.get_by_role('button', name='关闭详情').click()
    page.locator('.quota-card').focus()
    page.keyboard.press('Enter')
    assert page.get_by_role('dialog').count() == 1
    page.keyboard.press('Escape')
    assert page.get_by_role('dialog').count() == 0
    assert page.locator('.quota-card').evaluate('n=>document.activeElement===n && n.matches(":focus-visible") && getComputedStyle(n).outlineStyle!=="none"')
    results['browser_click_and_keyboard_unchanged'] = True
    assert not errors, errors
    results['browser_errors'] = len(errors)
    browser.close()

(ROOT / 'artifacts/validation/desktop-input-check.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(results, ensure_ascii=False))
