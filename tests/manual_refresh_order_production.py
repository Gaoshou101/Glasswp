"""Verify the upgraded live bridge, one upstream refresh, and reversible display order persistence."""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
OUT = ROOT / 'artifacts/validation'
errors, results = [], {}
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport={'width':1000,'height':1200})
    page = context.new_page()
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    cfg = page.evaluate("async () => await api('config/read')")
    original = [{'id':a['id'], 'alias':a['alias']} for a in cfg['accounts']]
    assert len(original) == 3
    token = cfg['readToken']
    changed = False
    try:
        page.locator('.order-item').nth(1).locator('button').first.click()
        changed = True
        page.locator('#save-account-order').click()
        page.wait_for_function("document.querySelector('#account-order-status').textContent.includes('已保存')")
        page.reload(wait_until='networkidle')
        expected = [original[1]['id'],original[0]['id'],original[2]['id']]
        assert page.locator('.order-item').evaluate_all('(rows) => rows.map(n => n.dataset.orderId)') == expected
        page.wait_for_function("""async ({base,token,ids}) => {
          const data=await (await fetch(base+'/v1/snapshot',{headers:{'X-Wallpaper-Token':token}})).json();
          return JSON.stringify(data.quota.accounts.map(a=>a.id)) === JSON.stringify(ids);
        }""", arg={'base':BASE,'token':token,'ids':expected}, polling=1000, timeout=60000)
        results['real_order_saved_reopened_and_snapshot_reordered'] = True
    finally:
        if changed:
            page.evaluate("async accounts => await api('config', {accounts})", original)
            page.wait_for_function("""async ({base,token,ids}) => {
              const data=await (await fetch(base+'/v1/snapshot',{headers:{'X-Wallpaper-Token':token}})).json();
              return JSON.stringify(data.quota.accounts.map(a=>a.id)) === JSON.stringify(ids);
            }""", arg={'base':BASE,'token':token,'ids':[a['id'] for a in original]}, polling=1000, timeout=60000)
            results['original_order_restored'] = True
    page.reload(wait_until='networkidle')
    page.locator('#account-order-heading').locator('..').screenshot(path=str(OUT / 'account-order-production.png'))
    before = page.evaluate("async () => (await api('quota-refresh/status')).state")
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps({
        'version':1,'bridgeUrl':BASE,'readToken':token,'quotaLayout':'cards','ignoreCodexBaseWeekly':True,'rotateSeconds':0
    })) + ');')
    page.set_viewport_size({'width':1600,'height':2560})
    page.goto(BASE + '/?wallpaper', wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    page.wait_for_function("!document.querySelector('#refresh-quota').disabled")
    page.locator('#refresh-quota').click()
    page.wait_for_function("document.querySelector('#refresh-quota').textContent.includes('刷新中')")
    page.wait_for_function("document.querySelector('#refresh-quota').textContent.includes('已刷新')", timeout=90000)
    report = context.request.get(BASE + '/v1/quota-refresh/status',headers={'X-Wallpaper-Token':token}).json()
    state = report['refresh']['state']
    assert state['status'] == 'ok' and len(state['accounts']) == 3 and all(a['status']=='ok' for a in state['accounts'])
    assert state['lastAttemptAt'] != before['lastAttemptAt']
    assert report['snapshotFetchedAt'] >= state['lastFinishedAt']
    assert page.locator('.quota-card').evaluate_all('(rows)=>rows.map(n=>n.dataset.accountId)') == [a['id'] for a in original]
    results['real_manual_upstream_refresh'] = {'status':state['status'],'accountsSucceeded':3,'lastAttemptAt':state['lastAttemptAt'],'lastFinishedAt':state['lastFinishedAt'],'snapshotFetchedAt':report['snapshotFetchedAt']}
    assert page.locator('.note').is_hidden()
    page.locator('.quota').screenshot(path=str(OUT / 'manual-refresh-quota-production.png'))
    page.screenshot(path=str(OUT / 'manual-refresh-wallpaper-production.png'))
    assert not errors,errors
    results['browser_errors'] = 0
    browser.close()

(OUT / 'manual-refresh-order-production.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print('Live bridge passed: order save/reopen/snapshot with original order restored; manual upstream refresh 3/3; no browser errors.')
