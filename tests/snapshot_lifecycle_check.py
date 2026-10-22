"""Anonymous production-bundle lifecycle checks; no live bridge or upstream writes."""
import json
import sys
import time
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

BASE = 'http://127.0.0.1:17632'
iso = lambda offset=0: time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time()+offset))
metric = lambda value: dict(value=value, unit='%', source='example', sensorId=None)
device = dict(name='Example', load=metric(20), temperature=metric(40), usedBytes=None, totalBytes=None)
snapshot = dict(schemaVersion=1, generatedAt=iso(), revisions=dict(epoch='fixture', hardware=1, weather=1, quota=1),
    hardware=dict(status='ok', observedAt=iso(), cpu=device, gpu=device, vram=device, ram=device, message=''),
    weather=dict(status='ok', fetchedAt=iso(), observedAt=iso(), city='Example', temperature=23, low=19, high=26, code=2, isDay=True, message=''),
    quota=dict(status='empty', fetchedAt=iso(), accounts=[], message='Example'))
errors, calls, held = [], [], []
mode = dict(hold=False)
with sync_playwright() as p:
    browser=p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    context=browser.new_context(viewport=dict(width=1600,height=2560))
    context.add_init_script('localStorage.setItem("wallpaper.dashboard.v1",'+json.dumps(json.dumps(dict(version=1,readToken='example',rotateSeconds=0)))+');')
    context.add_init_script('''window.formatterBuilds=0; const Formatter=Intl.DateTimeFormat;
        Intl.DateTimeFormat=new Proxy(Formatter,{construct(target,args){window.formatterBuilds++;return Reflect.construct(target,args)}});
        window.weatherWrites=0; const original=Element.prototype.setAttribute;
        Element.prototype.setAttribute=function(name,value){if(name==='title'&&this.classList.contains('weather'))window.weatherWrites++;return original.call(this,name,value)};
        window.activeIntervals=new Set(); const si=window.setInterval,ci=window.clearInterval;
        window.setInterval=(...args)=>{const id=si(...args);window.activeIntervals.add(id);return id};
        window.clearInterval=id=>{window.activeIntervals.delete(id);return ci(id)};''')
    page=context.new_page(); page.on('pageerror',lambda error:errors.append(str(error)))
    def respond(route):
        if urlsplit(route.request.url).path=='/v1/snapshot':
            calls.append(route.request.headers.get('x-wallpaper-token'))
            if mode['hold']:
                held.append(route)
                return
            snapshot['hardware']['observedAt']=iso()
            snapshot['revisions']['hardware']+=1
            route.fulfill(json=snapshot)
        else:
            route.fulfill(json=dict(canRefresh=False,refresh=dict(state=dict(status='idle'))))
    page.route(BASE+'/**',respond)
    page.goto((ROOT/'dist/index.html').as_uri(),wait_until='networkidle')
    page.wait_for_function('document.querySelector(".temperature").textContent.includes("23")')
    before=page.evaluate('({formats:formatterBuilds,writes:weatherWrites})')
    page.wait_for_timeout(4500)
    after=page.evaluate('({formats:formatterBuilds,writes:weatherWrites})')
    assert after==before, (before,after)
    assert page.evaluate('activeIntervals.size')==1, 'Liquid mode should share one lifecycle interval'
    page.evaluate('window.wallpaperPropertyListener.setPaused(true)')
    at_pause=len(calls); page.wait_for_timeout(2200)
    assert len(calls)==at_pause
    mode['hold']=True
    page.evaluate('window.wallpaperPropertyListener.setPaused(false)')
    page.wait_for_function('document.querySelector(".wallpaper").dataset.paused==="false"')
    page.wait_for_timeout(150)
    assert held
    page.evaluate('window.wallpaperPropertyListener.setPaused(true);window.wallpaperPropertyListener.setPaused(false)')
    page.wait_for_timeout(150)
    assert len(held)>=2
    # The old response may fail delivery because AbortController already cancelled it.
    try: held.pop(0).fulfill(json={**snapshot,'weather':{**snapshot['weather'],'temperature':99}})
    except Exception: pass
    mode['hold']=False
    held.pop().fulfill(json=snapshot)
    page.wait_for_timeout(150)
    assert '99' not in page.locator('.temperature').inner_text()
    assert page.evaluate('activeIntervals.size')==1
    assert not errors,errors
    browser.close()
report=dict(module_updates_skip_unchanged_data=True,formatter_reuse=True,single_lifecycle_interval=True,pause_cancels_polling=True,late_response_isolated=True,browser_errors=errors)
out=ROOT/'artifacts/validation/snapshot-lifecycle.json';out.parent.mkdir(parents=True,exist_ok=True)
out.write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report))
