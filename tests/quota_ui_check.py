"""Check current quotas, packed three-slot pages, and manual pagination."""
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'artifacts/validation/quota-current-20261023'
OUT.mkdir(parents=True, exist_ok=True)
BASE = 'http://127.0.0.1:17632'
iso = lambda offset=0: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time() + offset))
metric = dict(value=20, unit='%', source='example', sensorId=None)
device = dict(name='Example', load=metric, temperature=metric, usedBytes=None, totalBytes=None)
window = dict(id='short', label='5 小时窗口', remainingPercent=45, usedValue=None, limitValue=None, quotaUnit='',
              resetAt=iso(3600), durationSeconds=18000, mode='fixed', accuracy='exact', observedAt=iso(), stale=False)
accounts = [dict(id=str(i), alias=f'example-account-{i+1}@example.com', provider='codex' if i < 3 else 'antigravity', plan='Plus' if i < 3 else 'Antigravity',
                 windows=[dict(window, id=str(j), label=['Claude/GPT · 5 小时', 'Gemini · 5 小时', 'Claude/GPT · 周额度', 'Gemini · 周额度', 'Gemini 4 Argon · 周额度'][j] if i >= 3 else ['5 小时窗口', '周窗口', '日窗口 · 绘图（Premium）', '周窗口 · 基础模型'][j], remainingPercent=63 if j == 4 else 100 if i >= 3 else 80-j*10,
                               observedAt=iso(), stale=False) for j in range(count)]) for i, count in enumerate([4, 2, 1, 5, 4, 4, 2])]
snapshot = dict(schemaVersion=1, generatedAt=iso(), quota=dict(status='stale', fetchedAt=iso(), message='', accounts=accounts),
                hardware=dict(status='ok', observedAt=iso(), cpu=device, gpu=device, vram=device, ram=device, message=''),
                weather=dict(status='ok', fetchedAt=iso(), observedAt=iso(), city='示例城市', temperature=23, low=19, high=26, code=2, isDay=True, message=''))
errors = []
results = {}
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport=dict(width=1600, height=2560))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps(dict(version=1, readToken='anonymous', rotateSeconds=0, quotaLayout='cards', materialMode='liquid'))) + ');')
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
    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({})')
    assert page.locator('.quota-nav span').inner_text() == '1 / 3'
    next_button = page.get_by_role('button', name='下一页额度')
    next_button.click()
    page.wait_for_function('document.querySelector(".quota-nav span").textContent === "2 / 3"')
    assert page.locator('.quota-card').count() == 3
    assert page.locator('.quota-card[data-account-id="4"] .quota-window').count() == 4
    assert page.locator('.quota-card[data-account-id="3"] .quota-window').count() == 5
    assert page.evaluate('!document.activeElement.closest(".quota-nav")'), 'Mouse replay left pagination focus behind'
    page.mouse.move(5, 5)
    page.wait_for_timeout(350)
    page.locator('.quota').screenshot(path=str(OUT / 'five-windows-argon.png'))
    dimensions = []
    for width, height in [(800,1280), (1080,1920), (1200,1600), (1600,2560)]:
        page.set_viewport_size(dict(width=width, height=height))
        card_size = None
        for layout in ['cards', 'carousel']:
            page.evaluate("layout => window.wallpaperPropertyListener.applyUserProperties({quota_layout:{value:layout}})", layout)
            for index in range(3 if layout == 'cards' else 7):
                page.mouse.move(5, 5)
                page.wait_for_timeout(350)
                geometry = page.locator('.quota-card,.quota-account').evaluate_all('''nodes => nodes.map(n => {
                    const outer = (n.closest('.quota-card') || n.closest('.quota')).getBoundingClientRect();
                    const textClipped = [...n.querySelectorAll('.account-alias,.plan,.window-title,.remaining,.reset,.card-freshness:not([hidden])')].filter(w => {
                        const range = document.createRange(); range.selectNodeContents(w);
                        const text = range.getBoundingClientRect();
                        for (let clip = w; clip && clip !== n.parentElement; clip = clip.parentElement) {
                            if (!/hidden|clip/.test(getComputedStyle(clip).overflowY)) continue;
                            const box = clip.getBoundingClientRect();
                            if (text.top < box.top - 1 || text.bottom > box.bottom + 1) return true;
                        }
                        return false;
                    }).map(w => ({class:w.className,height:w.getBoundingClientRect().height,scroll:w.scrollHeight}));
                    const rows = [...n.querySelectorAll(':scope > .account-heading,:scope > .account-meta,:scope > .windows')].map(w => w.getBoundingClientRect());
                    const plan = n.querySelector('.plan').getBoundingClientRect();
                    const notice = n.querySelector('.card-freshness');
                    const noticeBox = notice.getBoundingClientRect();
                    return {account:n.dataset.accountId, count:n.querySelectorAll('.quota-window').length,
                        width:n.getBoundingClientRect().width, height:n.getBoundingClientRect().height,
                        inlineNotice:notice.hidden || (notice.closest('.account-meta') && notice.textContent === '数据已过期' && notice.title.includes('采集时间：') && noticeBox.left >= plan.right && Math.abs(noticeBox.top + noticeBox.height/2 - plan.top - plan.height/2) < 1),
                        textClipped, overlaps:rows.some((r,i) => i > 0 && r.top < rows[i-1].bottom - 1),
                        fits:[...n.querySelectorAll('.quota-window,.card-freshness:not([hidden])')].every(w => {const b=w.getBoundingClientRect();return b.bottom <= outer.bottom-2 && b.right<=outer.right && b.left>=outer.left})};
                })''')
                assert all(g['fits'] for g in geometry), (width, height, layout, index, geometry)
                assert all(not g['textClipped'] and not g['overlaps'] for g in geometry), (width, height, layout, index, geometry)
                assert all(g['inlineNotice'] for g in geometry), (width, height, layout, index, geometry)
                if layout == 'cards':
                    columns = page.locator('.quota-cards').evaluate('n=>getComputedStyle(n).gridTemplateColumns.split(" ").length')
                    assert columns == 3, (width, height, index, columns)
                    if card_size is None:
                        card_size = (geometry[0]['width'], geometry[0]['height'])
                    assert all(abs(g['width']-card_size[0]) < 1 and abs(g['height']-card_size[1]) < 1 for g in geometry), (width, height, index, card_size, geometry)
                    if (width, height) == (1600, 2560):
                        page.locator('.quota').screenshot(path=str(OUT / f'fixed-slots-page-{index+1}.png'))
                if index < (2 if layout == 'cards' else 6):
                    before = page.locator('.quota-nav span').inner_text()
                    next_button.click()
                    page.wait_for_function('old => document.querySelector(".quota-nav span").textContent !== old', arg=before)
            dimensions.append(dict(width=width, height=height, layout=layout, fits=True))
    results['all_windows_fit_cards_and_carousel'] = dimensions
    results['same_card_dimensions_and_three_slots_on_every_page'] = True
    results['only_current_quotas_have_no_expired_notice'] = True
    page.evaluate("window.wallpaperPropertyListener.applyUserProperties({quota_layout:{value:'cards'}})")
    previous = page.get_by_role('button', name='上一页额度')
    box = previous.bounding_box()
    page.mouse.move(box['x']+box['width']/2, box['y']+box['height']/2)
    page.mouse.up()
    page.wait_for_function('document.querySelector(".quota-nav span").textContent === "3 / 3"')
    assert page.evaluate('!document.activeElement.closest(".quota-nav")')
    results['native_and_release_only_mouse_leave_no_focus'] = True
    previous.focus()
    assert previous.evaluate('n=>n.matches(":focus-visible")')
    page.keyboard.press('Enter')
    page.wait_for_function('document.querySelector(".quota-nav span").textContent === "2 / 3"')
    assert previous.evaluate('n=>n===document.activeElement'), 'Keyboard focus must survive pagination'
    results['keyboard_focus_retained'] = True
    next_button.click()
    page.wait_for_function('document.querySelector(".quota-nav span").textContent === "3 / 3"')
    assert page.evaluate('JSON.parse(localStorage.getItem("wallpaper.dashboard.v1")).rotateSeconds') == 0
    page.clock.install()
    page.mouse.move(5, 5)
    page.clock.fast_forward(65000)
    page.wait_for_timeout(400)
    assert page.locator('.quota-nav span').inner_text() == '3 / 3'
    results['automatic_paging_disabled_even_after_old_interval'] = True
    page.evaluate("window.wallpaperPropertyListener.applyUserProperties({quota_layout:{value:'cards'}})")
    # Inventories above five still have reachable continuation cards and packed pages.
    for account in accounts[3:]:
        account['windows'].append(dict(window, id='gemini-shared', label='Gemini · 共享额度',
                                       durationSeconds=None, mode='unknown', remainingPercent=37))
    accounts[4]['windows'].append(dict(window, id='extra-budget', label='额外模型 · 周额度'))
    with page.expect_response(lambda response: response.url == BASE + '/v1/snapshot'):
        page.evaluate('window.wallpaperPropertyListener.setPaused(false)')
    page.wait_for_timeout(400)
    displayed = []
    for index in range(3):
        if index:
            next_button.click()
            page.wait_for_function('index => document.querySelector(".quota-nav span").textContent === `${index+1} / 3`', arg=index)
        page.mouse.move(5, 5)
        page.wait_for_timeout(350)
        displayed.extend(page.locator('.quota-card').evaluate_all('nodes=>nodes.map(n=>({id:n.dataset.accountId,labels:[...n.querySelectorAll(".window-title")].map(w=>w.textContent)}))'))
        assert page.locator('.quota-card').count() == 3, 'Overflow cards must fill every available slot before creating another page'
        assert page.locator('.quota-cards').evaluate('n=>getComputedStyle(n).gridTemplateColumns.split(" ").length') == 3
        assert all(abs(size['width']-card_size[0]) < 1 and abs(size['height']-card_size[1]) < 1 for size in page.locator('.quota-card').evaluate_all('nodes=>nodes.map(n=>({width:n.getBoundingClientRect().width,height:n.getBoundingClientRect().height}))'))
        page.locator('.quota').screenshot(path=str(OUT / f'shared-page-{index+1}.png'))
    for account in accounts:
        assert [label for entry in displayed if entry['id'] == account['id'] for label in entry['labels']] == [w['label'] for w in account['windows']]
    assert page.locator('.quota-card .window-title').all_text_contents()[-1] == 'Gemini · 共享额度'
    results['fresh_shared_and_overflow_windows_each_reachable_once_on_full_pages'] = True
    for account in accounts[4:6]:
        account['windows'].extend([dict(window, id=f'history-{j}', label=f'历史 Gemini {j}', observedAt=iso(-14*86400), stale=True) for j in range(2)])
    with page.expect_response(lambda response: response.url == BASE + '/v1/snapshot'):
        page.evaluate('window.wallpaperPropertyListener.setPaused(false)')
    page.wait_for_timeout(400)
    assert page.locator('.quota-nav span').inner_text() == '3 / 3'
    assert page.locator('.card-freshness:visible').count() == 0
    assert '历史' not in page.locator('.quota').inner_text()
    assert page.locator('.quota-card').count() == 3
    results['stale_history_does_not_add_pages_or_expired_notices'] = True
    # Stop receiving new evidence and let the visible observation expire.
    # Expiration must also remove balances without requiring a changed snapshot.
    page.clock.fast_forward(16*60*1000)
    page.wait_for_timeout(400)
    assert page.locator('.quota-window').count() == 0
    assert '暂无最新额度，等待刷新' in page.locator('.quota').inner_text()
    results['expired_balances_removed_without_waiting_for_snapshot_revision'] = True
    assert not errors, errors
    results['browserErrors'] = errors
    (OUT / 'result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(results, ensure_ascii=False))
    browser.close()
