"""Production DOM/motion checks with anonymous data; never modifies live settings."""
import copy
import json
import sys
import time
from datetime import datetime
from zoneinfo import ZoneInfo
from pathlib import Path
from urllib.parse import urlsplit, parse_qs

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'artifacts/validation'
OUT.mkdir(parents=True, exist_ok=True)
BASE = 'http://127.0.0.1:17632'
now = time.time()
iso = lambda offset=0: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(now + offset))
metric = lambda value, unit='%': dict(value=value, unit=unit, source='example', sensorId=None)
device = lambda name, load, temp, used=None, total=None: dict(name=name, load=metric(load), temperature=metric(temp, '°C'), usedBytes=used, totalBytes=total)
window = lambda ident, label, remaining, seconds, duration: dict(id=ident, label=label, remainingPercent=remaining, usedValue=None, limitValue=None, quotaUnit='', resetAt=iso(seconds), durationSeconds=duration, mode='fixed', accuracy='exact', observedAt=iso(), stale=False)
fixture = dict(schemaVersion=1, generatedAt=iso(),
    hardware=dict(status='ok', observedAt=iso(), cpu=device('i5-12600KF', 18, 48), gpu=device('RX 6900 XT', 12, 43), vram=device('专用显存', 25, None, 4*1024**3, 16*1024**3), ram=device('物理内存', 38, None, 18.24*1024**3, 48*1024**3), message='示例数据'),
    weather=dict(status='ok', fetchedAt=iso(), observedAt=iso(), city='苏州', temperature=23, low=19, high=26, code=2, isDay=True, message='示例天气', timezone='Asia/Shanghai', hourly=[dict(time=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime(int(now//3600)*3600+i*3600)),temperature=None if i==3 else 23-i*.3,precipitationProbability=None if i==3 else (65 if 4<=i<=7 else 0),precipitationMm=None if i==3 else (1.2 if 4<=i<=7 else 0),code=61 if 4<=i<=7 else 2) for i in range(24)]),
    quota=dict(status='ok', fetchedAt=iso(), message='预览示例', accounts=[dict(id=f'example-{i}', alias=f'示例账号 {letter}', provider='codex', plan=plan, windows=[window('short', '5 小时窗口', remaining, 7200, 18000), window('week', '周窗口', weekly, 259200, 604800)]) for i, letter, plan, remaining, weekly in [(1, 'A', 'Pro', 84, 67), (2, 'B', 'Pro', 96, 81), (3, 'C', 'Plus', 72, 45)]]))
for index, account in enumerate(fixture['quota']['accounts']):
    account['resetCredits'] = dict(available=[2, 0, 3][index], observedAt=iso(), stale=False)
activity_calls = []
activity_queries = []
activity_fixture = lambda ident: dict(accountId=ident, status='ok', fetchedAt=iso(), **{'from':iso(-7*86400)}, to=iso(), requests=1870 if ident=='example-1' else 1679, successRate=93.7, tokens=191200000, estimatedCost=91.61, lastActivity=iso(-30), recent=[dict(time=iso(-30-i*60), model='gpt-6-sol' if i%2==0 else 'gpt-6-luna', success=i%3!=1, statusCode=429 if i%3==1 else None, tokens=None if i==3 else 0 if i==2 else 12000+i*100, inputTokens=11000, outputTokens=1000, cachedTokens=8000, **({} if i==1 else dict(ttftMs=None if i==3 else 0 if i==2 else 750)), durationMs=None if i==3 else 0 if i==2 else 1234+i*100) for i in range(20)], hasMore=True, message='最近7天')
results = {}
errors = []

def select_period(page, period):
    labels = dict(today='今天', yesterday='昨天', **{'7d':'最近 7 天', '14d':'最近 14 天', '30d':'最近 30 天'}, all='全部', custom='自定义')
    page.get_by_role('combobox', name='请求时间范围').click()
    page.get_by_role('option', name=labels[period], exact=True).click()

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context = browser.new_context(viewport=dict(width=1600, height=2560))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",' + json.dumps(json.dumps(dict(version=1, readToken='preview-example', rotateSeconds=0, materialMode='liquid', material={g:dict(transparency=83,blur=26,edge=68) for g in ['top','quota','hardware']}))) + ');')
    # Count all JS frame callbacks, including demand-driven light interpolation.
    context.add_init_script('window.frameCallbacks=0; const nativeRAF=window.requestAnimationFrame.bind(window); window.requestAnimationFrame=cb=>nativeRAF(t=>{window.frameCallbacks++;cb(t)});')
    page = context.new_page()
    page.on('pageerror', lambda e: errors.append(str(e)))
    def respond(route):
        if route.request.url.endswith('/v1/desktop-input'):
            route.fulfill(json=dict(available=True, allowed=True))
        elif route.request.url.endswith('/v1/snapshot'):
            route.fulfill(json=fixture)
        elif '/v1/accounts/' in route.request.url and urlsplit(route.request.url).path.endswith('/activity'):
            url=urlsplit(route.request.url)
            ident=url.path.split('/')[-2]
            query={key:values[0] for key,values in parse_qs(url.query).items()}
            activity_queries.append(query)
            activity_calls.append(ident)
            payload=activity_fixture(ident)
            period=query.get('range','7d')
            payload['requests']={'today':100,'yesterday':200,'14d':2470,'30d':5470,'all':10470,'custom':2}.get(period,payload['requests'])
            payload.update(nextBeforeMs=int((now-1200)*1000),nextBeforeId=42,totalCount=payload['requests'])
            if query.get('beforeId'):
                payload['recent']=[{**row,'time':iso(-1230-i*60),'model':'older-page'} for i,row in enumerate(payload['recent'])]
                payload['hasMore']=False
            route.fulfill(json=payload)
        else:
            route.fulfill(json=dict(canRefresh=True, snapshotFetchedAt=fixture['quota']['fetchedAt'], refresh=dict(state=dict(status='idle', message='', lastFinishedAt=None))))
    page.route(BASE + '/**', respond)
    page.goto((ROOT / 'dist/index.html').as_uri(), wait_until='networkidle')
    page.wait_for_selector('.quota-card')
    assert page.locator('.wallpaper').get_attribute('data-material-mode') == 'liquid'
    page.evaluate('window.keptCard=document.querySelector(".quota-card"); window.keptFill=document.querySelector(".fill");')
    fixture['quota']['accounts'][0]['windows'][0]['remainingPercent'] = 51
    page.wait_for_function('document.querySelector(".remaining").textContent.includes("51%")')
    assert page.evaluate('keptCard===document.querySelector(".quota-card") && keptFill===document.querySelector(".fill")')
    assert 'scaleX(0.51)' == page.locator('.fill').first.evaluate('(n)=>n.style.transform')
    results['snapshot_keeps_card_and_progress_identity'] = True
    fixture['quota']['accounts'][0]['windows'][0]['accuracy'] = 'unknown'
    page.wait_for_function('document.querySelector(".reset").textContent.startsWith("参考 ")')
    assert '倒计时仅供参考' in page.locator('.quota-window').first.get_attribute('title')
    results['reference_and_unknown_semantics_retained'] = True

    fixture['hardware']['observedAt'] = iso(2)
    page.wait_for_selector('.sparkline')
    page.evaluate('window.keptTrend=document.querySelector(".sparkline")')
    fixture['hardware']['observedAt'] = iso(4)
    fixture['hardware']['cpu']['load']['value'] = 24
    page.wait_for_function('document.querySelector(".hardware-value").textContent==="24%"')
    assert page.evaluate('keptTrend===document.querySelector(".sparkline")')
    results['trend_svg_identity_retained'] = True

    original_layout = page.locator('.quota').evaluate('n=>n.style.transform')
    # Observe the event handler's entry position before a frame advances it.
    page.evaluate('document.querySelector(".wallpaper").addEventListener("pointermove",()=>{window.pointerLightX=parseFloat(document.querySelector(".clock").style.getPropertyValue("--light-x"))})')
    bounds = page.locator('.clock').bounding_box()
    page.mouse.move(bounds['x']+bounds['width']*.8, bounds['y']+bounds['height']*.3)
    page.wait_for_function('document.querySelector(".clock").classList.contains("liquid-hover")')
    light_x = lambda: page.locator('.clock').evaluate('n=>parseFloat(n.style.getPropertyValue("--light-x"))')
    entry_x = light_x()
    assert abs(entry_x - bounds['width']*.3*.44) < 1
    page.wait_for_timeout(500)
    page.mouse.move(bounds['x']+bounds['width']*.2, bounds['y']+bounds['height']*.6)
    page.wait_for_timeout(110)
    intermediate = light_x()
    assert -entry_x + 1 < intermediate < entry_x - 1, (entry_x, intermediate)
    page.wait_for_timeout(900)
    assert abs(light_x() + entry_x) < 1
    settled = page.evaluate('frameCallbacks')
    page.wait_for_timeout(500)
    assert page.evaluate('frameCallbacks') == settled
    assert not page.evaluate('document.querySelector(".clock").getAnimations({subtree:true}).some(a=>a.playState==="running")')
    assert page.locator('.quota').evaluate('n=>n.style.transform') == original_layout
    results['light_follows_mouse_then_raf_stops'] = True
    results['light_enters_at_cursor_and_eases_to_rest'] = True

    last_x = light_x()
    page.mouse.move(1, 1)
    page.wait_for_timeout(100)
    fading = page.locator('.clock .liquid-glint').evaluate('n=>Number(getComputedStyle(n,"::before").opacity)')
    assert 0 < fading < 1
    page.mouse.move(bounds['x']+bounds['width']*.8, bounds['y']+bounds['height']*.3)
    assert abs(page.evaluate('pointerLightX') - last_x) < .1
    page.wait_for_timeout(950)
    assert abs(light_x() - entry_x) < 1
    page.mouse.move(1, 1)
    page.wait_for_timeout(500)
    assert page.locator('.clock .liquid-glint').evaluate('n=>Number(getComputedStyle(n,"::before").opacity)') == 0
    page.mouse.move(bounds['x']+bounds['width']*.8, bounds['y']+bounds['height']*.3)
    assert abs(light_x() - entry_x) < 1
    results['light_exit_fades_and_fast_reentry_is_continuous'] = True

    weather = page.locator('.weather').bounding_box()
    for i in range(6):
        box = weather if i % 2 == 0 else bounds
        page.mouse.move(box['x']+box['width']*.7, box['y']+box['height']*.5)
    assert page.locator('.liquid-hover').count() == 1
    assert 'liquid-hover' in page.locator('.clock').get_attribute('class')
    page.wait_for_timeout(1000)
    settled = page.evaluate('frameCallbacks')
    page.wait_for_timeout(400)
    assert page.evaluate('frameCallbacks') == settled
    results['rapid_card_crossings_leave_no_animation_loop'] = True
    page.mouse.down()
    assert 'liquid-pressed' in page.locator('.clock').get_attribute('class')
    page.mouse.up()
    assert 'liquid-pressed' not in page.locator('.clock').get_attribute('class')
    results['press_release_feedback'] = True

    page.locator('.quota-card').first.click()
    assert page.get_by_role('dialog').count() == 1
    assert page.get_by_role('dialog').evaluate('n=>n.getAnimations().some(a=>a.playState==="running" && a.effect.getTiming().duration===380)')
    page.wait_for_selector('.request-activity-table')
    assert page.get_by_role('dialog').locator('.activity-metric').count() == 5
    assert page.locator('[data-metric=requests] .activity-metric-value').inner_text() == '1.9K'
    assert page.locator('[data-metric=success] .activity-metric-value').inner_text() == '93.7%'
    assert page.locator('[data-metric=tokens] .activity-metric-value').inner_text() == '191.2M'
    assert page.locator('[data-metric=cost] .activity-metric-value').inner_text() == '$91.61'
    assert page.locator('.reset-credit-badge span').inner_text() == '2'
    assert page.locator('.request-activity-table tbody tr').count() == 20
    assert page.locator('.request-activity-table tbody tr').nth(2).locator('td').nth(4).inner_text() == '0'
    assert page.locator('.request-activity-table th').last.inner_text() == '首字 / 耗时'
    assert page.locator('.request-activity-table tbody tr').nth(0).locator('td').last.inner_text() == '0.8s / 1.2s'
    assert page.locator('.request-activity-table tbody tr').nth(1).locator('td').last.inner_text() == '— / 1.3s'
    assert page.locator('.request-activity-table tbody tr').nth(2).locator('td').last.inner_text() == '0.0s / 0.0s'
    assert page.locator('.request-activity-table tbody tr').nth(3).locator('td').last.inner_text() == '— / —'
    assert '失败 429' in page.locator('.request-activity-table').inner_text()
    assert activity_calls == ['example-1']
    page.locator('.request-table-scroll').evaluate('n=>n.scrollTop=120')
    kept_scroll=page.locator('.request-table-scroll').evaluate('n=>n.scrollTop')
    page.wait_for_timeout(1200)
    assert page.locator('.request-table-scroll').evaluate('n=>n.scrollTop') == kept_scroll
    assert activity_calls == ['example-1']
    assert page.get_by_role('dialog').get_by_role('button', name='更新活动').is_enabled()
    assert page.get_by_role('dialog').get_by_role('link', name='账号设置').get_attribute('href') == BASE+'/admin/#account-order-heading'
    assert page.get_by_role('dialog').evaluate('n=>n.parentElement.classList.contains("detail-layer")')
    assert page.get_by_role('dialog').evaluate('n=>getComputedStyle(n).backdropFilter.includes("blur")')
    assert page.get_by_role('dialog').evaluate('n=>getComputedStyle(n).backgroundColor.startsWith("rgba")')
    page.wait_for_timeout(450)
    panel = page.get_by_role('dialog').bounding_box()
    quota = page.locator('.quota').bounding_box()
    assert panel['y'] >= quota['y'] and panel['y']+panel['height'] <= quota['y']+quota['height']
    page.keyboard.press('Escape')
    assert page.get_by_role('dialog').count() == 0
    assert page.locator('.quota-card').first.evaluate('n=>n===document.activeElement')
    assert page.locator('.detail-closing').count() == 1
    assert page.locator('.detail-closing').evaluate('n=>n.inert && n.getAnimations().some(a=>a.effect.getTiming().duration===190)')
    page.wait_for_timeout(240)
    assert page.locator('.card-details').count() == 0
    for _ in range(3):
        page.locator('.quota-card').first.evaluate('n=>n.click()')
        page.keyboard.press('Escape')
        page.locator('.hardware-item').first.evaluate('n=>n.click()')
        assert page.locator('.card-details').count() == 1
        page.keyboard.press('Escape')
    page.wait_for_timeout(240)
    assert page.locator('.card-details').count() == 0
    results['quota_details_inside_widget_and_focus_restored'] = True
    results['account_details_show_scoped_activity_overview_and_request_rows'] = True
    results['request_rows_preserve_zero_unknown_failure_and_scroll'] = True
    results['details_float_as_translucent_glass'] = True
    results['details_animate_open_close_and_rapid_switches_leave_no_panels'] = True
    before_closed=len(activity_calls)
    page.wait_for_timeout(1100)
    assert len(activity_calls)==before_closed
    page.locator('.quota-card').nth(1).click()
    page.wait_for_function('document.querySelector("[data-metric=requests] .activity-metric-value").textContent==="1.7K"')
    assert page.locator('.reset-credit-badge span').inner_text() == '0'
    assert activity_calls[-1] == 'example-2'
    page.keyboard.press('Escape')
    results['activity_loads_only_for_open_account_and_keeps_real_zero_resets'] = True

    # A previous account's late response must never populate the newly opened account.
    page.evaluate('''payload=>{window.activityNativeFetch=window.fetch;window.fetch=(...args)=>String(args[0]).includes('/example-1/activity')?new Promise(resolve=>{window.finishOldActivity=()=>resolve(new Response(JSON.stringify(payload),{status:200,headers:{"Content-Type":"application/json"}}))}):window.activityNativeFetch(...args)}''', activity_fixture('example-1'))
    page.locator('.quota-card').first.click()
    page.wait_for_function('typeof finishOldActivity==="function"')
    page.keyboard.press('Escape')
    page.locator('.quota-card').nth(1).click()
    page.wait_for_function('document.querySelector("[data-metric=requests] .activity-metric-value").textContent==="1.7K"')
    page.evaluate('finishOldActivity()')
    page.wait_for_timeout(100)
    assert page.locator('[data-metric=requests] .activity-metric-value').inner_text()=='1.7K'
    assert '示例账号 B' in page.get_by_role('dialog').get_attribute('aria-label')
    page.keyboard.press('Escape')
    page.evaluate('()=>{window.fetch=window.activityNativeFetch}')
    results['late_account_response_cannot_replace_selected_account'] = True
    page.locator('.quota-card').first.click()
    page.wait_for_selector('.request-activity-table')
    assert page.get_by_role('combobox', name='请求时间范围').get_attribute('data-value')=='7d'
    assert page.locator('.activity-range-option').count()==7
    # Native select painting crashes the real WE CEF host before any menu input.
    assert page.locator('.card-details select').count()==0
    page.get_by_role('combobox', name='请求时间范围').focus()
    page.keyboard.press('ArrowDown')
    assert page.get_by_role('listbox', name='请求时间范围选项').is_visible()
    page.keyboard.press('End')
    assert page.get_by_role('option', name='自定义', exact=True).evaluate('n=>n===document.activeElement')
    page.keyboard.press('Home')
    page.keyboard.press('ArrowDown')
    page.keyboard.press('Enter')
    page.wait_for_function('document.querySelector("[data-metric=requests] .activity-metric-value").textContent==="200"')
    page.get_by_role('combobox', name='请求时间范围').click()
    page.keyboard.press('Escape')
    assert page.get_by_role('dialog').count()==1
    assert page.get_by_role('combobox', name='请求时间范围').get_attribute('aria-expanded')=='false'
    assert page.get_by_role('combobox', name='请求时间范围').evaluate('n=>n===document.activeElement')
    results['cef_safe_range_menu_keyboard_selection_and_escape'] = True
    for period, count in [('today','100'),('yesterday','200'),('14d','2.5K'),('30d','5.5K'),('all','10.5K')]:
        page.get_by_role('combobox', name='请求时间范围').focus()
        select_period(page, period)
        page.wait_for_function('(count)=>document.querySelector("[data-metric=requests] .activity-metric-value").textContent===count',arg=count)
        assert activity_queries[-1]['range']==period
        assert page.get_by_role('combobox', name='请求时间范围').evaluate('n=>n===document.activeElement')
    select_period(page, 'custom')
    assert '选择起止时间' in page.locator('.activity-empty').inner_text()
    before_custom=len(activity_queries)
    page.get_by_label('开始时间', exact=True).fill('2026-10-14T12:00')
    page.get_by_label('结束时间', exact=True).fill('2026-10-14T11:00')
    page.get_by_role('button', name='应用', exact=True).click()
    assert '开始时间必须早于结束时间' in page.get_by_role('alert').inner_text()
    assert len(activity_queries)==before_custom
    page.get_by_label('结束时间', exact=True).fill('2026-10-14T13:00')
    page.get_by_role('button', name='应用', exact=True).click()
    page.wait_for_selector('.request-activity-table')
    assert activity_queries[-1]['range']=='custom' and int(activity_queries[-1]['toMs'])-int(activity_queries[-1]['fromMs'])==3600000
    select_period(page, '7d')
    page.wait_for_function('document.querySelector("[data-metric=requests] .activity-metric-value").textContent==="1.9K"')
    initial_last=page.locator('[data-metric=last] .activity-metric-value').inner_text()
    page.get_by_role('button', name='较早请求', exact=True).click()
    page.wait_for_function('document.querySelector(".request-model")?.textContent==="older-page"')
    assert activity_queries[-1]['beforeId']=='42' and 'fromMs' in activity_queries[-1] and 'toMs' in activity_queries[-1]
    assert page.locator('.activity-page-number').inner_text()=='第 2 页'
    assert page.locator('[data-metric=last] .activity-metric-value').inner_text()==initial_last
    assert page.get_by_role('button', name='较早请求', exact=True).is_disabled()
    page.get_by_role('button', name='较新请求', exact=True).click()
    page.wait_for_function('document.querySelector(".request-model")?.textContent==="gpt-6-sol"')
    # Ignore a late response to another range even if the transport ignores abort.
    page.evaluate('''payload=>{window.rangeNativeFetch=window.fetch;window.fetch=(...args)=>String(args[0]).includes('range=today')?new Promise(resolve=>{window.finishOldRange=()=>resolve(new Response(JSON.stringify(payload),{status:200,headers:{"Content-Type":"application/json"}}))}):window.rangeNativeFetch(...args)}''',activity_fixture('example-1'))
    select_period(page, 'today')
    page.wait_for_function('typeof finishOldRange==="function"')
    select_period(page, '30d')
    page.wait_for_function('document.querySelector("[data-metric=requests] .activity-metric-value").textContent==="5.5K"')
    page.evaluate('finishOldRange()')
    page.wait_for_timeout(100)
    assert page.locator('[data-metric=requests] .activity-metric-value').inner_text()=='5.5K'
    page.evaluate('()=>{window.fetch=window.rangeNativeFetch}')
    select_period(page, '7d')
    page.wait_for_selector('.request-activity-table')
    page.keyboard.press('Escape')
    results['cpamp_range_presets_custom_validation_and_focus'] = True
    results['cursor_pagination_preserves_overview_and_ignores_old_range'] = True
    page.locator('.hardware-item').first.focus()
    page.keyboard.press('Enter')
    assert 'i5-12600KF' in page.get_by_role('dialog').inner_text()
    assert page.get_by_role('dialog').locator('.detail-trend').count() == 1
    assert '样本均值' in page.get_by_role('dialog').inner_text()
    assert '峰值 / 低值' in page.get_by_role('dialog').inner_text()
    page.get_by_role('dialog').locator('.detail-trend').focus()
    page.keyboard.press('Home')
    assert '使用率 18%' in page.locator('.chart-readout').inner_text()
    page.evaluate('window.keptDetailChart=document.querySelector(".detail-trend")')
    fixture['hardware']['observedAt'] = iso(6)
    fixture['hardware']['cpu']['load']['value'] = 60
    page.wait_for_function('document.querySelector(".hardware-value").textContent==="60%"')
    assert page.evaluate('keptDetailChart===document.querySelector(".detail-trend") && keptDetailChart===document.activeElement')
    page.keyboard.press('End')
    assert '使用率 60%' in page.locator('.chart-readout').inner_text()
    page.get_by_role('button', name='关闭详情').click()
    results['hardware_details_keyboard'] = True
    results['hardware_sample_statistics_and_chart_focus_survive_updates'] = True
    page.locator('.weather').click()
    assert page.get_by_role('dialog').count() == 1
    assert page.get_by_role('dialog').locator('.forecast-hour').count() == 12
    assert page.get_by_role('dialog').get_by_role('link', name='切换城市').get_attribute('href') == BASE + '/admin/#city-query'
    assert page.locator('.forecast-hour').nth(0).locator('.forecast-probability').inner_text() == '0%'
    assert page.locator('.forecast-hour').nth(3).locator('.forecast-probability').inner_text() == '—'
    assert page.locator('.forecast-hour').nth(3).locator('.forecast-temperature').inner_text() == '—'
    assert page.locator('.forecast-hour').nth(4).locator('.forecast-amount').inner_text() == '1.2 mm'
    local_day = datetime.fromtimestamp(now, ZoneInfo('Asia/Shanghai')).date()
    expected_cross_day = any(datetime.fromisoformat(hour['time'].replace('Z', '+00:00')).astimezone(ZoneInfo('Asia/Shanghai')).date() != local_day for hour in fixture['weather']['hourly'][:12])
    assert ('明日' in page.locator('.forecast-grid').inner_text()) == expected_cross_day
    page.wait_for_timeout(450)
    page.get_by_role('dialog').screenshot(path=str(OUT / 'liquid-weather-insight-1600.png'))
    page.keyboard.press('Escape')
    results['weather_hourly_forecast_preserves_zero_unknown_and_rain_amount'] = True
    old_hours = fixture['weather']['hourly']
    fixture['weather']['hourly'] = None
    page.wait_for_timeout(2300)
    page.locator('.weather').click()
    assert '小时预报暂不可用' in page.get_by_role('dialog').inner_text()
    fixture['weather']['hourly'] = old_hours
    page.wait_for_function('document.querySelectorAll(".forecast-hour").length===12')
    assert page.locator('.forecast-hour').nth(0).locator('.forecast-probability').inner_text() == '0%'
    page.keyboard.press('Escape')
    results['old_bridge_without_hourly_forecast_has_honest_empty_state'] = True
    results['hourly_forecast_recovers_in_open_panel_after_empty_state'] = True

    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({quota_x:{value:2},quota_scale:{value:110}})')
    layout = page.locator('.quota').evaluate('n=>n.style.transform')
    page.locator('.quota-card').first.hover()
    assert page.locator('.quota').evaluate('n=>n.style.transform') == layout
    page.locator('.weather').evaluate('n=>n.click()')
    page.evaluate('window.wallpaperPropertyListener.setPaused(true)')
    page.wait_for_timeout(250)
    paused_frames = page.evaluate('frameCallbacks')
    page.wait_for_timeout(450)
    assert page.evaluate('frameCallbacks') == paused_frames
    assert page.locator('.wallpaper').get_attribute('data-dynamic') == 'false'
    assert not page.evaluate('document.querySelector(".wallpaper").getAnimations({subtree:true}).filter(a=>a.playState==="running").length')
    page.keyboard.press('Escape')
    assert page.locator('.card-details').count() == 0
    page.evaluate('window.wallpaperPropertyListener.setPaused(false)')
    page.emulate_media(reduced_motion='reduce')
    page.wait_for_function('document.querySelector(".wallpaper").dataset.dynamic==="false"')
    assert page.locator('.wallpaper').get_attribute('data-dynamic') == 'false'
    page.emulate_media(reduced_motion='no-preference')
    page.wait_for_function('document.querySelector(".wallpaper").dataset.dynamic==="true"')
    assert page.locator('.wallpaper').get_attribute('data-dynamic') == 'true'
    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({motion:{value:false}})')
    assert page.locator('.wallpaper').get_attribute('data-dynamic') == 'false'
    results['position_pause_reduced_motion_and_switch'] = True
    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({motion:{value:true},quota_x:{value:0},quota_scale:{value:100}})')

    # Exercise true page changes with more than 3 accounts; snapshot polls must not cancel the animation.
    extra = copy.deepcopy(fixture['quota']['accounts'][0]); extra['id'] = 'extra'; extra['alias'] = '示例账号 D'
    fixture['quota']['accounts'].append(extra)
    page.wait_for_selector('.quota-nav')
    nav = page.locator('.quota-nav').bounding_box()
    page.mouse.move(nav['x'], nav['y'])
    page.get_by_role('button', name='下一页额度', exact=True).click()
    page.wait_for_function('document.querySelector("[aria-label=下一页额度]")===document.activeElement')
    assert page.locator('.quota-card').count() == 1
    assert page.locator('.quota-card').first.get_attribute('data-account-id') == 'extra'
    assert page.evaluate('document.querySelector("#quota-content").getAnimations().length') > 0
    page.wait_for_timeout(350)
    assert not page.evaluate('document.querySelector("#quota-content").getAnimations().length')
    results['real_page_change_animates_once'] = True
    results['paging_preserves_keyboard_focus'] = True
    page.get_by_role('button', name='上一页额度', exact=True).click()
    fixture['quota']['accounts'].pop()
    fixture['quota']['accounts'][0]['windows'][0]['remainingPercent'] = 84
    fixture['quota']['accounts'][0]['windows'][0]['accuracy'] = 'exact'
    page.wait_for_function('!document.querySelector(".quota-nav")')
    page.mouse.move(1, 1)
    page.locator('.quota-card').first.evaluate('n=>n.blur()')
    page.wait_for_timeout(400)

    audit = '''() => {
      const r=n=>n.getBoundingClientRect(), canvas=r(document.querySelector('.wallpaper'));
      const regions=['.top','.quota','.hardware'].map(s=>r(document.querySelector(s)));
      const overflow=[...document.querySelectorAll('.time,.date,.temperature,.remaining,.reset,.hardware-value,.hardware-detail')].filter(n=>n.scrollWidth>n.clientWidth+2).map(n=>n.className);
      return {inside:regions.every(b=>b.left>=canvas.left-1&&b.right<=canvas.right+1&&b.top>=canvas.top-1&&b.bottom<=canvas.bottom+1),gap:regions[1].top-regions[0].bottom,overflow};
    }'''
    results['layouts'] = {}
    for width, height in [(800,1280), (1080,1920), (1600,2560), (2160,3840)]:
        page.set_viewport_size(dict(width=width,height=height))
        page.wait_for_timeout(250)
        check = page.evaluate(audit)
        assert check['inside'] and not check['overflow'] and check['gap'] > 0, (width, height, check)
        results['layouts'][f'{width}x{height}'] = check
        # New analytical panels must remain inside their widget at every target size.
        page.locator('.quota-card').first.click()
        page.wait_for_timeout(450)
        page.wait_for_selector('.request-activity-table')
        assert page.locator('.card-details-body').evaluate('n=>n.scrollWidth<=n.clientWidth+1&&n.scrollHeight<=n.clientHeight+1')
        qpanel = page.get_by_role('dialog').bounding_box()
        region = page.locator('.quota').bounding_box()
        assert qpanel['x'] >= region['x'] and qpanel['x']+qpanel['width'] <= region['x']+region['width']+1
        assert qpanel['y'] >= region['y'] and qpanel['y']+qpanel['height'] <= region['y']+region['height']+1
        if width in [800, 1600]:
            page.get_by_role('dialog').screenshot(path=str(OUT / f'liquid-quota-insight-{width}.png'))
        select_period(page, 'custom')
        page.wait_for_selector('.activity-custom-range')
        assert page.locator('.card-details-body').evaluate('n=>n.scrollWidth<=n.clientWidth+1&&n.scrollHeight<=n.clientHeight+1')
        if width in [800, 1600]:
            page.get_by_role('dialog').screenshot(path=str(OUT / f'liquid-quota-custom-{width}.png'))
        select_period(page, '7d')
        page.keyboard.press('Escape')
        page.locator('.hardware-item').first.click()
        page.wait_for_timeout(450)
        assert page.locator('.card-details-body').evaluate('n=>n.scrollWidth <= n.clientWidth+1')
        if width in [800, 1600]:
            page.get_by_role('dialog').screenshot(path=str(OUT / f'liquid-hardware-insight-{width}.png'))
        fit = page.locator('.card-details-body').evaluate('n=>({scroll:n.scrollHeight,client:n.clientHeight,u:getComputedStyle(n).getPropertyValue("--u")})')
        assert fit['scroll'] <= fit['client']+1, (width, fit)
        page.keyboard.press('Escape')
        page.locator('.weather').click()
        page.wait_for_timeout(450)
        fit = page.locator('.card-details-body').evaluate('n=>({scroll:n.scrollHeight,client:n.clientHeight,width:n.clientWidth,fullWidth:n.scrollWidth})')
        assert fit['fullWidth'] <= fit['width']+1 and fit['scroll'] <= fit['client']+1, (width, fit)
        wp = page.get_by_role('dialog').bounding_box()
        top_region = page.locator('.top').bounding_box()
        assert wp['y'] >= top_region['y']-1 and wp['y']+wp['height'] <= top_region['y']+top_region['height']+1
        if width in [800,1600]:
            page.get_by_role('dialog').screenshot(path=str(OUT / f'liquid-weather-insight-{width}.png'))
        page.keyboard.press('Escape')
        page.mouse.move(1,1)
        page.locator('.hardware-item').first.evaluate('n=>n.blur()')
        if width == 1600:
            page.screenshot(path=str(OUT / 'liquid-glass-1600x2560.png'))
            page.locator('.quota-card').first.hover(position=dict(x=170,y=90))
            page.wait_for_timeout(400)
            page.screenshot(path=str(OUT / 'liquid-glass-hover.png'))
            page.mouse.move(1,1)
    page.evaluate('window.wallpaperPropertyListener.applyUserProperties({material_mode:{value:"frosted"}})')
    assert page.locator('.wallpaper').get_attribute('data-material-mode') == 'frosted'
    assert page.locator('.wallpaper').get_attribute('data-dynamic') == 'false'
    results['frosted_comparison_available'] = True
    results['analytical_panels_fit_four_portrait_sizes'] = True
    assert not errors, errors
    browser.close()
results['browser_errors'] = len(errors)
(OUT / 'liquid-glass-check.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(results, ensure_ascii=False))
