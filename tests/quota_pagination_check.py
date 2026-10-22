"""Check uneven quota pagination against a frozen real bridge snapshot."""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
OUT = ROOT / 'artifacts/validation/quota-pagination-20261020'
OUT.mkdir(parents=True, exist_ok=True)
errors = []
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport={'width': 1600, 'height': 2560})
    if os.environ.get('WALLPAPER_TEST_BUILT_SCRIPT') == '1':
        context.route('**/wallpaper.js', lambda route: route.fulfill(path=str(ROOT / 'dist/wallpaper.js'), content_type='application/javascript'))
    page = context.new_page()
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    cfg = page.evaluate("async () => await api('config/read')")
    live = page.request.get(BASE + '/v1/snapshot', headers={'X-Wallpaper-Token': cfg['readToken']}).json()
    accounts = live['quota']['accounts']
    assert accounts, 'Live bridge has no accounts to verify'
    expected = []
    for start in range(0, len(accounts), 3):
        group = accounts[start:start + 3]
        for offset in range(0, max(1, max(len(a['windows']) for a in group)), 2):
            expected.append([
                {'id': a['id'], 'labels': [w['label'] for w in a['windows'][offset:offset + 2]]}
                for a in group if offset == 0 or len(a['windows']) > offset
            ])
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps({
        'version': 1, 'readToken': cfg['readToken'], 'rotateSeconds': 0, 'quotaLayout': 'cards',
        'ignoreCodexBaseWeekly': False, 'ignoreCodexImageQuota': False
    })) + ');')
    # Freeze one real snapshot so a live refresh cannot alter the pages mid-check.
    context.route('**/v1/snapshot', lambda route: route.fulfill(json=live))
    page.goto(BASE + '/?wallpaper', wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    def cards():
        return page.locator('.quota-card').evaluate_all("nodes => nodes.map(n => ({id:n.dataset.accountId,labels:[...n.querySelectorAll('.window-title')].map(w=>w.textContent)}))")
    displayed_pages = []
    for index, expected_cards in enumerate(expected):
        if index:
            page.get_by_role('button', name='下一页额度').click()
        displayed = cards()
        assert displayed == expected_cards, (index, displayed, expected_cards)
        displayed_pages.append(displayed)
        page.locator('.quota').screenshot(path=str(OUT / f'page-{index + 1}.png'))
    for account in accounts:
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
            assert cards() == expected_cards
        for expected_cards in expected[1:]:
            page.get_by_role('button', name='下一页额度').click()
            assert cards() == expected_cards
    else:
        assert page.locator('.quota-nav').count() == 0
    assert not errors, errors
    result = {'builtScriptOverride': os.environ.get('WALLPAPER_TEST_BUILT_SCRIPT') == '1',
              'accountWindowCounts': [len(a['windows']) for a in accounts],
              'pageCardCounts': [len(displayed) for displayed in displayed_pages],
              'noRepeatedWindows': True, 'allQuotaWindowsRetained': True,
              'backAndForward': True, 'refreshKeepsSelectedPage': True, 'browserErrors': errors}
    (OUT / 'result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))
    browser.close()
