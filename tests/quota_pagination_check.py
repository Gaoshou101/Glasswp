"""Check uneven quota pagination against a frozen real bridge snapshot."""
import json
import os
import sys
import time
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
OUT = ROOT / 'artifacts/validation/quota-current-pagination-20261023'
OUT.mkdir(parents=True, exist_ok=True)
errors = []
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport={'width': 1600, 'height': 2560})
    if os.environ.get('WALLPAPER_TEST_BUILT_SCRIPT') == '1':
        context.route('**/wallpaper.js', lambda route: route.fulfill(path=str(ROOT / 'dist/wallpaper.js'), content_type='application/javascript'))
        context.route('**/wallpaper.css', lambda route: route.fulfill(path=str(ROOT / 'dist/wallpaper.css'), content_type='text/css'))
    page = context.new_page()
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    cfg = page.evaluate("async () => await api('config/read')")
    live = page.request.get(BASE + '/v1/snapshot', headers={'X-Wallpaper-Token': cfg['readToken']}).json()
    accounts = live['quota']['accounts']
    assert accounts, 'Live bridge has no accounts to verify'
    now = time.time()
    def current(window):
        try:
            age = now - datetime.fromisoformat(window['observedAt'].replace('Z', '+00:00')).timestamp()
            return not window['stale'] and -60 <= age <= 900
        except (TypeError, ValueError, KeyError):
            return False
    visible = [dict(account, windows=[w for w in account['windows'] if current(w)]) for account in accounts]
    entries = []
    for start in range(0, len(visible), 3):
        group = visible[start:start + 3]
        for offset in range(0, max(1, max(len(a['windows']) for a in group)), 5):
            entries.extend([
                {'id': a['id'], 'labels': [w['label'] for w in a['windows'][offset:offset + 5]]}
                for a in group if offset == 0 or len(a['windows']) > offset
            ])
    expected = [entries[i:i+3] for i in range(0, len(entries), 3)]
    assert all(len(page) == 3 for page in expected[:-1])
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps({
        'version': 1, 'readToken': cfg['readToken'], 'rotateSeconds': 0, 'quotaLayout': 'cards',
        'ignoreCodexBaseWeekly': False, 'ignoreCodexImageQuota': False
    })) + ');')
    # Freeze one real snapshot so a live refresh cannot alter the pages mid-check.
    context.route('**/v1/snapshot', lambda route: route.fulfill(json=live))
    # Simulated WE mode needs an approved host-input response; native guard has its own checks.
    context.route('**/v1/desktop-input', lambda route: route.fulfill(json={'available': True, 'allowed': True}))
    page.goto(BASE + '/?wallpaper', wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    def cards():
        return page.locator('.quota-card').evaluate_all("nodes => nodes.map(n => ({id:n.dataset.accountId,labels:[...n.querySelectorAll('.window-title')].map(w=>w.textContent)}))")
    def wait_cards(expected_cards):
        page.wait_for_function("expected => JSON.stringify([...document.querySelectorAll('.quota-card')].map(n => ({id:n.dataset.accountId,labels:[...n.querySelectorAll('.window-title')].map(w=>w.textContent)}))) === JSON.stringify(expected)", arg=expected_cards)
    displayed_pages = []
    for index, expected_cards in enumerate(expected):
        if index:
            page.get_by_role('button', name='下一页额度').click()
            wait_cards(expected_cards)
        displayed = cards()
        assert displayed == expected_cards, (index, displayed, expected_cards)
        page.mouse.move(5, 5)
        page.wait_for_timeout(350)
        clipped = page.locator('.quota-card').evaluate_all('''cards => cards.flatMap(card => {
            const outer = card.getBoundingClientRect();
            return [...card.querySelectorAll('.account-alias,.plan,.quota-window,.reset,.card-freshness:not([hidden])')].flatMap(node => {
                const box = node.getBoundingClientRect();
                const range = document.createRange(); range.selectNodeContents(node);
                const text = range.getBoundingClientRect();
                if (box.top < outer.top || box.bottom > outer.bottom) return [node.className];
                if (/hidden|clip/.test(getComputedStyle(node).overflowY) && (text.top < box.top - 1 || text.bottom > box.bottom + 1)) return [node.className];
                return [];
            });
        })''')
        assert not clipped, (index, clipped)
        displayed_pages.append(displayed)
        page.locator('.quota').screenshot(path=str(OUT / f'page-{index + 1}.png'))
    for account in visible:
        displayed = [label for displayed_page in displayed_pages for card in displayed_page
                     if card['id'] == account['id'] for label in card['labels']]
        assert displayed == [w['label'] for w in account['windows']], displayed
    # Periodic refresh preserves the selected extra page and its account set.
    with page.expect_response(lambda response: response.url == BASE + '/v1/snapshot'):
        page.evaluate('window.wallpaperPropertyListener.setPaused(false)')
    page.wait_for_timeout(200)
    assert cards() == expected[-1]
    if len(expected) > 1:
        for expected_cards in reversed(expected[:-1]):
            page.get_by_role('button', name='上一页额度').click()
            wait_cards(expected_cards)
            assert cards() == expected_cards
        for expected_cards in expected[1:]:
            page.get_by_role('button', name='下一页额度').click()
            wait_cards(expected_cards)
            assert cards() == expected_cards
    else:
        assert page.locator('.quota-nav').count() == 0
    assert not errors, errors
    result = {'builtScriptOverride': os.environ.get('WALLPAPER_TEST_BUILT_SCRIPT') == '1',
              'serverAccountWindowCounts': [len(a['windows']) for a in accounts],
              'currentAccountWindowCounts': [len(a['windows']) for a in visible],
              'pageCardCounts': [len(displayed) for displayed in displayed_pages],
              'noRepeatedWindows': True, 'allCurrentQuotaWindowsRetained': True,
              'expiredHistoryHidden': True, 'allNonFinalPagesHaveThreeCards': True,
              'allPagesTextFitsWithoutVerticalClipping': True,
              'backAndForward': True, 'refreshKeepsSelectedPage': True, 'browserErrors': errors}
    (OUT / 'result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))
    browser.close()
