"""Visual and state checks for the compact wallpaper layout against the live bridge."""
import copy
import json
import os
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
FRONTEND = os.environ.get('WALLPAPER_TEST_FRONTEND', BASE + '/?wallpaper')
OUT = ROOT / 'artifacts/validation' / ('liquid-regression' if 'WALLPAPER_TEST_FRONTEND' in os.environ else '')
OUT.mkdir(parents=True, exist_ok=True)
errors = []
results = {}
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport={'width': 1600, 'height': 2560})
    page = context.new_page()
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    cfg = page.evaluate("async () => await api('config/read')")
    live = page.request.get(BASE + '/v1/snapshot', headers={'X-Wallpaper-Token': cfg['readToken']}).json()
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps({
        'version': 1, 'readToken': cfg['readToken'], 'rotateSeconds': 0, 'quotaLayout': 'cards', 'ignoreCodexBaseWeekly': True
    })) + ');')
    page.goto(FRONTEND, wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert page.locator('.note').is_hidden()
    assert page.locator('.quota-footer').count() == 0
    assert '余额与重置时间以最近证据为准' not in page.locator('.wallpaper').inner_text()
    assert 'CPAMP 额度已连接' not in page.locator('.wallpaper').inner_text()
    assert page.locator('.weather-caption').inner_text() in ['苏州', '苏州 · 上次天气']
    page.screenshot(path=str(OUT / 'clean-layout-live-1600x2560.png'))
    page.locator('.quota').screenshot(path=str(OUT / 'clean-quota-live.png'))
    results['live_compact_wallpaper'] = True

    now = time.time()
    iso = lambda n: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(n))
    short = dict(id='five-hour', label='5 小时窗口', remainingPercent=13, usedValue=None, limitValue=None, quotaUnit='',
                 resetAt=iso(now+7000), durationSeconds=18000, mode='fixed', accuracy='derived', observedAt=iso(now), stale=False)
    weekly = dict(short, id='weekly', label='周窗口', remainingPercent=81, resetAt=iso(now+400000), durationSeconds=604800, accuracy='exact')
    base = dict(weekly, id='gpt-reserve-weekly-0', label='周窗口 · 基础模型', observedAt=iso(now-90000), stale=True)
    fixture = copy.deepcopy(live)
    fixture.pop('revisions', None)  # Mutable legacy fixture must not carry frozen live revision IDs.
    fixture['quota'] = dict(status='ok', fetchedAt=iso(now), message='布局验证数据', accounts=[
        dict(id='first', alias='测试账号 A', provider='codex', plan='team', windows=[short, weekly, base]),
        dict(id='second', alias='这是一个用于验证省略与提示的很长账号别名', provider='codex', plan='team', windows=[weekly]),
        dict(id='third', alias='测试账号 C', provider='codex', plan='plus', windows=[short, weekly])])
    page.route('**/v1/snapshot', lambda route: route.fulfill(json=fixture))
    page.reload(wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert page.locator('.card-freshness:visible').count() == 0
    assert page.locator('.quota-nav').count() == 0
    assert page.locator('.missing-window').count() == 0
    results['healthy_state_has_no_redundant_captions'] = True

    def audit():
        return page.evaluate("""() => {
          const box = n => { const r = n.getBoundingClientRect(); return {x:r.x,y:r.y,right:r.right,bottom:r.bottom,width:r.width,height:r.height}; };
          const canvas=box(document.querySelector('.wallpaper')), top=box(document.querySelector('.top')), quota=box(document.querySelector('.quota')), hardware=box(document.querySelector('.hardware'));
          const clipped=[...document.querySelectorAll('.time,.date,.temperature,.remaining,.reset,.hardware-value,.hardware-detail')].filter(n=>n.scrollWidth>n.clientWidth+2).map(n=>n.className);
          for (const card of document.querySelectorAll('.quota-card')) for (const n of card.querySelectorAll('.account-heading,.plan,.quota-window,.card-freshness')) if (!n.hidden && box(n).bottom > box(card).bottom-6) clipped.push(n.className);
          for (const item of document.querySelectorAll('.hardware-item')) for (const n of item.children) if (!n.hidden && !n.classList.contains('liquid-glint') && box(n).bottom > box(item).bottom-5) clipped.push(n.className);
          const outside=[top,quota,hardware].some(r=>r.x<canvas.x-1||r.right>canvas.right+1||r.y<canvas.y||r.bottom>canvas.bottom+1);
          const overlap=top.bottom>=quota.y||quota.bottom>=hardware.y;
          return {canvas,top,quota,hardware,clipped,outside,overlap};
        }""")
    checks = []
    for width, height in [(720,1280),(800,1280),(900,1440),(1024,1600),(1080,1920),(1200,1920),(1440,2560),(1600,2560),(2160,3840),(1200,1600),(1080,2400),(1440,3440)]:
        page.set_viewport_size({'width':width,'height':height})
        page.wait_for_timeout(80)
        check = audit()
        check['size']=[width,height]
        assert not check['clipped'] and not check['outside'] and not check['overlap'], check
        checks.append(check)
    results['responsive_checks']=checks
    page.set_viewport_size({'width':1600,'height':2560})
    page.evaluate("window.wallpaperPropertyListener.applyUserProperties({ignore_codex_base_weekly:{value:false}})")
    assert page.locator('.quota-heading .quota-nav').count() == 1
    page.get_by_role('button',name='下一页额度').click()
    assert page.locator('.card-freshness:visible').count() == 1
    assert '数据已过期' in page.locator('.card-freshness:visible').inner_text()
    check=audit()
    assert not check['clipped'] and not check['outside'] and not check['overlap'],check
    results['pagination_in_heading_and_stale_evidence_visible']=True

    # Unknown quota, zero, empty accounts and disconnected data retain visible feedback.
    fixture['quota']['accounts'][0]['windows']=[dict(short,remainingPercent=0,accuracy='exact'),dict(weekly,remainingPercent=None)]
    fixture['quota']['accounts'][2]['windows']=[]
    page.reload(wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert page.locator('.remaining').first.inner_text() == '剩余0%'
    assert page.locator('.remaining').nth(1).inner_text() == '剩余—'
    assert '暂无额度证据' in page.locator('.quota-card').nth(2).inner_text()
    results['zero_unknown_and_empty_states_retained']=True

    page.evaluate("window.wallpaperPropertyListener.applyUserProperties({quota_layout:{value:'carousel'}})")
    check=audit()
    assert not check['clipped'] and not check['outside'] and not check['overlap'],check
    assert page.locator('.quota-heading .quota-nav').count() == 1
    results['carousel_layout']=True
    page.unroute('**/v1/snapshot')
    page.route('**/v1/snapshot',lambda route:route.abort())
    page.evaluate('window.wallpaperPropertyListener.setPaused(false)')
    page.wait_for_function("!document.querySelector('.note').hidden && document.querySelector('.note').textContent.includes('断开')",timeout=15000)
    results['disconnect_alert_visible']=True
    page.unroute('**/v1/snapshot')
    page.evaluate('window.wallpaperPropertyListener.setPaused(false)')
    page.wait_for_function("document.querySelector('.note').hidden",timeout=15000)
    results['recovery_hides_alert']=True
    assert not errors,errors
    results['browser_errors']=0
    browser.close()

(OUT / 'clean-layout-result.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print('Compact layout passed: 12 portrait sizes; pagination, stale, missing, zero, disconnect, recovery; no browser errors.')
