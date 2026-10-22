"""Exercise the upstream refresh settings against a staged bridge on port 17633.

Uses the user's selected accounts, without reading or printing management credentials.
"""
import json
from pathlib import Path
import sys
from datetime import datetime, timedelta

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools' / 'python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17633'
OUT = ROOT / 'artifacts' / 'validation'
OUT.mkdir(parents=True, exist_ok=True)
results = {}
errors = []

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    page = browser.new_page(viewport={'width': 1000, 'height': 1200})
    page.on('pageerror', lambda error: errors.append(str(error)))
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    assert page.locator('#quota-refresh-enabled').is_checked()
    assert page.locator('#quota-refresh-minutes').input_value() == '10'
    page.wait_for_function("document.querySelector('#quota-refresh-summary').textContent.includes('3/3')", timeout=60000)
    results['default_enabled_ten_minutes_real_accounts'] = True

    def report():
        return page.evaluate("async () => await api('quota-refresh/status')")

    initial = report()
    assert initial['state']['status'] == 'ok'
    assert len(initial['state']['accounts']) == 3
    # State polling may update output, but must not overwrite a pending input edit.
    page.locator('#quota-refresh-minutes').fill('47')
    page.wait_for_timeout(3500)
    assert page.locator('#quota-refresh-minutes').input_value() == '47'
    results['polling_preserves_unsaved_edit'] = True

    page.locator('#quota-refresh-minutes').fill('7')
    page.locator('#quota-refresh-enabled').uncheck()
    page.locator('#save-quota-refresh').click()
    page.wait_for_function("document.querySelector('#status').textContent === '刷新设置已保存'")
    disabled = report()
    assert disabled['intervalMinutes'] == 7 and not disabled['enabled'] and disabled['nextRefreshAt'] is None
    page.reload(wait_until='networkidle')
    page.wait_for_function("document.querySelector('#status').textContent.includes('已连接')")
    assert page.locator('#quota-refresh-minutes').input_value() == '7'
    assert not page.locator('#quota-refresh-enabled').is_checked()
    results['custom_interval_disabled_persisted'] = True

    page.locator('#quota-refresh-minutes').fill('0')
    page.locator('#save-quota-refresh').click()
    assert report()['intervalMinutes'] == 7
    results['invalid_interval_rejected'] = True
    page.locator('#quota-refresh-minutes').fill('7')

    # Explicit manual refresh still works while automatic refresh is off.
    page.locator('#refresh-quota-now').click()
    page.wait_for_function("document.querySelector('#refresh-quota-now').disabled")
    page.wait_for_function("!document.querySelector('#refresh-quota-now').disabled && document.querySelector('#quota-refresh-summary').textContent.includes('3/3')", timeout=60000)
    refreshed = report()
    assert refreshed['state']['lastSuccessAt'] > initial['state']['lastSuccessAt']
    assert refreshed['state']['status'] == 'ok' and refreshed['nextRefreshAt'] is None
    results['manual_upstream_refresh_all_three_accounts'] = True

    page.locator('#quota-refresh-minutes').fill('10')
    page.locator('#quota-refresh-enabled').check()
    page.locator('#save-quota-refresh').click()
    page.wait_for_function("document.querySelector('#status').textContent === '刷新设置已保存'")
    final = report()
    assert final['enabled'] and final['intervalMinutes'] == 10
    assert datetime.fromisoformat(final['nextRefreshAt']) - datetime.fromisoformat(final['state']['lastFinishedAt']) == timedelta(minutes=10)
    assert final['state']['lastAttemptAt'] == refreshed['state']['lastAttemptAt']
    results['reenable_default_schedules_ten_minutes_without_extra_call'] = True

    public_config = page.evaluate("async () => await api('config/read')")
    assert 'managementKey' not in public_config
    readonly_denied = page.request.post(BASE + '/v1/setup/quota-refresh', headers={'Origin': BASE, 'X-Wallpaper-Token': public_config['readToken']}, data={})
    assert readonly_denied.status == 403
    cross_origin = page.request.post(BASE + '/v1/setup/quota-refresh', headers={'Origin': 'https://example.test'}, data={})
    assert cross_origin.status == 403
    results['read_ticket_and_cross_origin_cannot_trigger_upstream'] = True

    snap = page.request.get(BASE + '/v1/snapshot', headers={'X-Wallpaper-Token': public_config['readToken']}).json()
    assert all(any(not w['stale'] for w in a['windows']) for a in snap['quota']['accounts'])
    results['fresh_observations_displayed'] = True
    page.locator('#management-key').evaluate("n => n.closest('section').style.display = 'none'")
    page.locator('#ticket').evaluate("n => n.closest('section').style.display = 'none'")
    page.screenshot(path=str(OUT / 'quota-refresh-settings.png'), full_page=True)
    assert not errors, errors
    results['browser_errors'] = 0
    browser.close()

(OUT / 'quota-refresh-result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(results, ensure_ascii=False, indent=2))
