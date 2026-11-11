"""Clock/weather sheet acceptance with anonymous fixtures; no live bridge writes."""
import json
import sys
import time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright
OUT = ROOT / 'artifacts/validation/top-details'
OUT.mkdir(parents=True, exist_ok=True)
BASE = 'http://127.0.0.1:17632'
iso = lambda offset=0: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time()+offset))
metric = dict(value=25,unit='%',source='example',sensorId=None)
device = dict(name='Example',load=metric,temperature=metric,usedBytes=None,totalBytes=None)
clock = dict(timezone='Asia/Shanghai',offsetSeconds=0,seconds=True,hour12=False,worldZones=['Asia/Shanghai','Asia/Tokyo','Asia/Singapore','Europe/London','America/New_York','America/Los_Angeles'])
weather = dict(status='ok',fetchedAt=iso(),observedAt=iso(),city='苏州',temperature=23,low=19,high=26,code=2,isDay=True,timezone='Asia/Shanghai',message='',hourly=[dict(time=iso(i*3600),temperature=None if i==3 else 23-i*.4,precipitationProbability=None if i==3 else 65 if i>5 else 0,precipitationMm=None if i==3 else 1.2 if i>5 else 0,code=61 if i>5 else 2) for i in range(12)])
snapshot=dict(schemaVersion=1,generatedAt=iso(),clock=clock,hardware=dict(status='ok',observedAt=iso(),cpu=device,gpu=device,vram=device,ram=device,message=''),quota=dict(status='ok',fetchedAt=iso(),accounts=[],message=''),weather=weather)
errors=[]
with sync_playwright() as p:
    browser=p.chromium.launch(headless=True,executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context=browser.new_context(viewport=dict(width=1600,height=2560))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",'+json.dumps(json.dumps(dict(version=2,readToken='anonymous',materialMode='liquid')))+');')
    page=context.new_page()
    page.on('pageerror',lambda e:errors.append(str(e)))
    def respond(route):
        route.fulfill(json=snapshot if route.request.url.endswith('/v1/snapshot') else dict(available=True,allowed=True,canRefresh=False,refresh=dict(state=dict(status='idle',message=''))))
    page.route(BASE+'/**',respond)
    page.goto((ROOT/'dist/index.html').as_uri(),wait_until='networkidle')
    page.wait_for_selector('.weather[data-detail-title]')
    for width,height in [(1600,2560),(800,1280),(1080,1920),(540,960)]:
        page.set_viewport_size(dict(width=width,height=height))
        for kind in ['clock','weather']:
            page.locator('.'+kind).click()
            page.wait_for_timeout(450)
            dialog=page.get_by_role('dialog')
            dialog.screenshot(path=str(OUT/f'{kind}-{width}.png'))
            fit=dialog.evaluate('n=>[...n.querySelectorAll(".card-details-body,.world-clock-layout,.world-clock-row,.weather-detail-layout,.weather-current,.weather-forecast,.forecast-hour")].map(e=>({class:e.className,h:e.clientHeight,sh:e.scrollHeight,w:e.clientWidth,sw:e.scrollWidth}))')
            assert fit[0]['sh'] <= fit[0]['h']+1, (width,kind,fit)
            assert all(item['sw'] <= item['w']+1 for item in fit), (width,kind,fit)
            if kind == 'weather':
                assert dialog.locator('.forecast-trend polyline').count() == 2  # Missing temperature breaks the line.
                assert dialog.locator('.forecast-probability').nth(0).inner_text() == '0%'
                assert dialog.locator('.forecast-probability').nth(3).inner_text() == '—'
                assert dialog.locator('.forecast-icon').count() == 12
            print(f'{width} {kind}: layout and data semantics passed',flush=True)
            page.keyboard.press('Escape')
            dialog.wait_for(state='hidden')
    page.set_viewport_size(dict(width=1600,height=2560))
    for seconds, hour12 in [(False,False),(False,True),(True,True)]:
        clock.update(seconds=seconds,hour12=hour12)
        page.wait_for_function('v=>document.querySelector(".time").dataset.seconds===String(v[0])&&document.querySelector(".time").dataset.hour12===String(v[1])',arg=[seconds,hour12])
        page.locator('.clock').click()
        assert page.locator('.card-details-body').evaluate('n=>n.scrollHeight<=n.clientHeight+1')
        assert page.locator('.world-clock-row').evaluate_all('nodes=>nodes.every(n=>n.scrollWidth<=n.clientWidth+1)')
        page.keyboard.press('Escape')
        page.get_by_role('dialog').wait_for(state='hidden')
    print('world clock 12/24 hour and seconds variants passed',flush=True)
    page.locator('.weather').click()
    weather.update(status='stale',fetchedAt=iso(-7200))
    page.wait_for_function('document.querySelector(".card-details").dataset.stale === "true"')
    assert '上次实况' in page.locator('.weather-current-status').inner_text()
    assert '天气连接待更新' in page.locator('.forecast-summary').inner_text()
    page.get_by_role('dialog').screenshot(path=str(OUT/'weather-stale.png'))
    weather.update(hourly=[],provider='amap')
    page.wait_for_function('document.querySelector(".weather-forecast").dataset.empty === "true"')
    assert '不提供小时预报' in page.locator('.forecast-empty').inner_text()
    assert page.locator('.weather-current-temperature').inner_text() == '23°'
    page.get_by_role('dialog').screenshot(path=str(OUT/'weather-empty.png'))
    assert page.locator('.card-details-body').evaluate('n=>n.scrollHeight<=n.clientHeight+1')
    page.keyboard.press('Escape')
    page.get_by_role('dialog').wait_for(state='hidden')
    page.emulate_media(reduced_motion='reduce')
    page.locator('.clock').click()
    assert page.locator('.wallpaper').get_attribute('data-dynamic') == 'false'
    assert page.get_by_role('dialog').evaluate('n=>n.getAnimations({subtree:true}).length') == 0
    page.keyboard.press('Escape')
    page.get_by_role('dialog').wait_for(state='hidden')
    print('stale, empty, reduced motion and close checks passed',flush=True)
    assert not errors, errors
    browser.close()
