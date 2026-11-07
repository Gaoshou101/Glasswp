# Used-up quota windows are named as exhausted until their reset instead of being shown as expired.
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'artifacts/validation/quota-exhausted'
OUT.mkdir(parents=True, exist_ok=True)
BASE = 'http://127.0.0.1:17632'
iso = lambda offset=0: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time() + offset))
metric = dict(value=20, unit='%', source='example', sensorId=None)
device = dict(name='Example', load=metric, temperature=metric, usedBytes=None, totalBytes=None)
def window(ident, label, remaining, observed, reset, stale, duration=18000):
    return dict(id=ident, label=label, remainingPercent=remaining, usedValue=None, limitValue=None, quotaUnit='', resetAt=reset,
                durationSeconds=duration, mode='fixed', accuracy='exact', observedAt=observed, stale=stale)
three_hours_ago = iso(-10800)
accounts = [
    # Old zero sample, reset still ahead: exhausted, and the card is not marked expired.
    dict(id='old-zero', alias='A', provider='codex', plan='Plus', windows=[window('short', '5 小时窗口', 0, three_hours_ago, iso(3600), True), window('weekly', '周窗口', 40, iso(), iso(86400), False, 604800)]),
    dict(id='fresh-zero', alias='B', provider='codex', plan='Plus', windows=[window('short', '5 小时窗口', 0, iso(), iso(3600), False)]),
    # An old non-zero sample may have changed: it stays hidden as before.
    dict(id='old-nonzero', alias='C', provider='codex', plan='Plus', windows=[window('short', '5 小时窗口', 30, three_hours_ago, iso(3600), True), window('weekly', '周窗口', 70, iso(), iso(86400), False, 604800)]),
    # The reset has passed without a new sample: the old zero no longer holds.
    dict(id='zero-reset-passed', alias='D', provider='codex', plan='Plus', windows=[window('short', '5 小时窗口', 0, three_hours_ago, iso(-60), True)]),
    dict(id='only-exhausted', alias='E', provider='codex', plan='Plus', windows=[window('short', '5 小时窗口', 0, three_hours_ago, iso(3600), True)]),
    # Reaches its reset during the check: the card must re-render without waiting for new data.
    dict(id='resets-soon', alias='F', provider='codex', plan='Plus', windows=[window('short', '5 小时窗口', 0, three_hours_ago, iso(20), True)]),
]
snapshot = dict(schemaVersion=1, generatedAt=iso(), quota=dict(status='stale', fetchedAt=iso(), message='', accounts=accounts),
                hardware=dict(status='ok', observedAt=iso(), cpu=device, gpu=device, vram=device, ram=device, message=''),
                weather=dict(status='ok', fetchedAt=iso(), observedAt=iso(), city='示例城市', temperature=23, low=19, high=26, code=2, isDay=True, message=''))
WINDOW_STATE = '''nodes => nodes.map(n => ({ title: n.querySelector('.window-title').textContent,
    remaining: n.querySelector('.remaining').textContent, exhausted: n.dataset.exhausted, label: getComputedStyle(n.querySelector('.remaining small')).display,
    reset: n.querySelector('.reset').textContent, tooltip: n.title }))'''
errors, results = [], {}
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport=dict(width=1600, height=2560))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps(dict(version=2, readToken='anonymous', rotateSeconds=0, quotaLayout='cards', materialMode='liquid'))) + ');')
    page = context.new_page()
    page.on('pageerror', lambda e: errors.append(str(e)))
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
    card = lambda ident: page.locator(f'.quota-card[data-account-id="{ident}"]')
    window_state = lambda ident: card(ident).locator('.quota-window').evaluate_all(WINDOW_STATE)
    def freshness(ident):
        node = card(ident).locator('.card-freshness')
        return None if node.get_attribute('hidden') is not None else node.inner_text()

    a = window_state('old-zero')
    assert [w['title'] for w in a] == ['5 小时窗口', '周窗口'], a
    assert a[0]['remaining'] == '已用尽' and a[0]['exhausted'] == 'true' and a[0]['label'] == 'none', a[0]
    assert a[0]['reset'].endswith('后重置') and '额度已用尽' in a[0]['tooltip'], a[0]
    assert a[1]['remaining'] == '剩余40%' and a[1]['exhausted'] == 'false', a[1]
    assert freshness('old-zero') is None, 'an exhausted window must not mark the card expired'
    results['old_zero_sample_named_exhausted_until_reset'] = True
    b = window_state('fresh-zero')
    assert b[0]['remaining'] == '已用尽' and b[0]['exhausted'] == 'true', b
    results['fresh_zero_named_exhausted'] = True
    c = window_state('old-nonzero')
    assert [w['title'] for w in c] == ['周窗口'], c
    results['old_nonzero_sample_still_hidden'] = True
    page.locator('.quota').screenshot(path=str(OUT / 'page-1.png'))

    page.get_by_role('button', name='下一页额度').click()
    page.wait_for_function('document.querySelector(".quota-nav span").textContent === "2 / 2"')
    assert card('zero-reset-passed').locator('.quota-window').count() == 0
    assert '暂无最新额度' in card('zero-reset-passed').locator('.empty-quota').inner_text()
    results['zero_after_reset_waits_for_refresh'] = True
    e = window_state('only-exhausted')
    assert e[0]['remaining'] == '已用尽' and freshness('only-exhausted') is None, (e, freshness('only-exhausted'))
    results['exhausted_only_card_not_marked_expired'] = True
    assert window_state('resets-soon')[0]['remaining'] == '已用尽'
    page.locator('.quota').screenshot(path=str(OUT / 'page-2.png'))
    page.wait_for_function('document.querySelectorAll(\'.quota-card[data-account-id="resets-soon"] .quota-window\').length === 0', timeout=40000)
    assert '暂无最新额度' in card('resets-soon').locator('.empty-quota').inner_text()
    results['card_rerenders_when_exhaustion_ends'] = True
    browser.close()
assert not errors, errors
(OUT / 'result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(results))
