"""Clock/weather editor acceptance using anonymous fixtures and an ephemeral local server."""
import copy
import functools
import http.server
import json
import sys
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'artifacts/validation'
OUT.mkdir(parents=True, exist_ok=True)


class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=str(ROOT / 'public')))
threading.Thread(target=server.serve_forever, daemon=True).start()
BASE = f'http://127.0.0.1:{server.server_address[1]}'
clock = dict(timezone='Asia/Shanghai', offsetSeconds=0, seconds=False, hour12=False,
             worldZones=['Asia/Shanghai', 'Asia/Tokyo', 'Asia/Singapore', 'Europe/London', 'America/New_York', 'America/Los_Angeles'])
report = dict(enabled=False, state=dict(status='idle', message='尚未刷新', accounts=[]))
cfg = dict(clock=copy.deepcopy(clock), weather=dict(provider='open-meteo', apiKeyConfigured=False), cpampUrl='https://example.test',
           managementConfigured=False, readToken='fixture-only', bridgeUrl=BASE, city=dict(id='1', name='测试城市'), sensors={},
           quotaRefreshEnabled=True, quotaRefreshMinutes=10, quotaRefresh=report, accounts=[], diagnostics={})
saved, errors, results = [], [], {}
state = dict(fail=False)


def respond(route):
    path = route.request.url.removeprefix(BASE)
    if path == '/v1/setup/session':
        route.fulfill(json=dict(session='fixture-session'))
    elif path == '/v1/setup/config/read':
        route.fulfill(json=cfg)
    elif path == '/v1/setup/quota-refresh/status':
        route.fulfill(json=report)
    elif path == '/v1/snapshot':
        route.fulfill(json=dict(quota=dict(accounts=[])))
    elif path == '/v1/setup/time/sync':
        route.fulfill(json=dict(offsetSeconds=2.125, measuredAt='2026-11-10T00:00:00Z', source='Fixture HTTPS Date', roundTripMs=64))
    elif path == '/v1/setup/config':
        if state['fail']:
            route.fulfill(status=500, json=dict(message='模拟保存失败'))
            return
        body = route.request.post_data_json
        if 'weather' in body:
            w = body['weather']
            if (w['provider'] == 'qweather' and cfg.get('weather', {}).get('provider') == 'qweather'
                    and w.get('apiHost', '').strip().lower() != (cfg['weather'].get('apiHost') or '').strip().lower()
                    and not w.get('apiKey') and not w.get('clearApiKey')):
                route.fulfill(status=400, json=dict(message='API Host 更改时必须重填密钥'))
                return
        saved.append(copy.deepcopy(body))
        if 'clock' in body:
            cfg['clock'] = body['clock']
        if 'weather' in body:
            w = body['weather']
            cfg['weather'] = dict(provider=w['provider'], apiKeyConfigured=bool(w.get('apiKey')) or
                                  (w['provider'] == cfg['weather']['provider'] and cfg['weather']['apiKeyConfigured'] and not w.get('clearApiKey')))
            cfg['weather']['apiHost'] = w.get('apiHost') if w['provider'] == 'qweather' else None
            cfg['weather']['cityCode'] = w.get('cityCode') if w['provider'] == 'amap' else None
        if 'quotaRefreshMinutes' in body:
            cfg['quotaRefreshMinutes'] = body['quotaRefreshMinutes']
            cfg['quotaRefreshEnabled'] = body['quotaRefreshEnabled']
        route.fulfill(json=dict(message='已保存'))
    else:
        raise AssertionError(path)


with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    page = browser.new_page(viewport=dict(width=1200, height=1000))
    page.on('pageerror', lambda error: errors.append(str(error)))
    page.on('dialog', lambda dialog: dialog.accept())
    page.route('**/v1/**', respond)
    page.goto(BASE + '/admin/', wait_until='networkidle')
    page.wait_for_function('config !== null')
    assert page.locator('#save-clock').is_disabled() and page.locator('#save-weather').is_disabled()
    assert page.locator('#weather-provider option').evaluate_all('(options) => options.map(o => o.value)') == ['open-meteo', 'met-no', 'openweathermap', 'weatherapi', 'amap', 'qweather']
    assert 'Asia/Shanghai' in page.locator('#clock-preview-date').inner_text()
    results['saved_state_loads_cleanly'] = True

    page.locator('#clock-timezone').fill('Asia/Kolkata')
    page.locator('#clock-offset').fill('-12.25')
    page.locator('#clock-seconds').check()
    page.locator('#clock-world-zones').fill('Europe/London\nAmerica/New_York\nEurope/London')
    assert page.locator('#save-clock').is_enabled() and '时间' in page.locator('#unsaved').inner_text()
    assert 'Asia/Kolkata' in page.locator('#clock-preview-date').inner_text()
    page.locator('#weather-provider').select_option('met-no')
    page.locator('#save-weather').click()
    page.wait_for_function('config.weather.provider === "met-no"')
    assert page.locator('#clock-timezone').input_value() == 'Asia/Kolkata'
    assert page.locator('#clock-offset').input_value() == '-12.25'
    assert page.locator('#save-clock').is_enabled() and page.locator('#save-weather').is_disabled()
    page.locator('#save-clock').click()
    page.wait_for_function('config.clock.timezone === "Asia/Kolkata"')
    assert saved[-1]['clock']['worldZones'] == ['Europe/London', 'America/New_York']
    assert saved[-1]['clock']['offsetSeconds'] == -12.25 and saved[-1]['clock']['seconds']
    assert page.locator('#save-clock').is_disabled()
    results['save_preserves_other_drafts_and_world_order'] = True

    before = len(saved)
    page.locator('#sync-clock').click()
    page.wait_for_function('document.getElementById("clock-offset").value === "2.125"')
    assert len(saved) == before and page.locator('#save-clock').is_enabled()
    assert '保存时间设置后生效' in page.locator('#clock-sync-status').inner_text()
    page.locator('#reset-clock-offset').click()
    assert page.locator('#clock-offset').input_value() == '0'
    page.locator('#cancel-clock').click()
    assert page.locator('#clock-offset').input_value() == '-12.25' and page.locator('#save-clock').is_disabled()
    results['sync_and_reset_are_unsaved_drafts'] = True

    for field, value, message in [('clock-timezone', 'invalid/timezone', '有效时区'), ('clock-offset', '86401', '时间校准'),
                                   ('clock-world-zones', 'Europe/London\nwrong/zone', '世界时钟时区无效')]:
        page.locator('#' + field).fill(value)
        page.locator('#save-clock').click()
        page.wait_for_function('(message) => document.getElementById("status").textContent.includes(message)', arg=message)
        assert len(saved) == before
        page.locator('#cancel-clock').click()
    page.locator('#clock-world-zones').fill('')
    page.locator('#save-clock').click()
    page.wait_for_function('config.clock.worldZones.length === 0')
    results['invalid_inputs_block_writes_and_empty_world_list_is_valid'] = True

    page.locator('#weather-provider').select_option('weatherapi')
    assert page.locator('#weather-key-field').is_visible()
    page.locator('#save-weather').click()
    page.wait_for_function('document.getElementById("status").textContent.includes("请填写当前天气来源")')
    assert len(saved) == before + 1
    page.locator('#weather-api-key').fill('fixture-key-never-real')
    state['fail'] = True
    page.locator('#save-weather').click()
    page.wait_for_function('document.getElementById("status").textContent === "模拟保存失败"')
    assert page.locator('#weather-api-key').input_value() == 'fixture-key-never-real' and page.locator('#save-weather').is_enabled()
    state['fail'] = False
    page.locator('#save-weather').click()
    page.wait_for_function('config.weather.provider === "weatherapi"')
    assert saved[-1] == dict(weather=dict(provider='weatherapi', apiKey='fixture-key-never-real'))
    assert page.locator('#weather-api-key').input_value() == '' and cfg['weather']['apiKeyConfigured']
    assert 'fixture-key' not in (page.evaluate('JSON.stringify(localStorage)') or '')
    results['keyed_provider_validates_retains_failed_draft_and_clears_after_save'] = True

    page.locator('#weather-api-key').fill('unsaved-key')
    page.locator('#weather-provider').select_option('openweathermap')
    assert page.locator('#weather-api-key').input_value() == '' and not cfg['weather'].get('apiKey')
    page.locator('#cancel-weather').click()
    assert page.locator('#weather-provider').input_value() == 'weatherapi' and page.locator('#save-weather').is_disabled()
    page.locator('#weather-clear-key').check()
    assert page.locator('#weather-api-key').is_disabled()
    page.locator('#save-weather').click()
    page.wait_for_function('!config.weather.apiKeyConfigured')
    assert saved[-1] == dict(weather=dict(provider='weatherapi', clearApiKey=True))
    results['switching_provider_discards_typed_key_and_explicit_clear_saves'] = True

    page.locator('#weather-provider').select_option('openweathermap')
    page.locator('#weather-api-key').fill('fixture-owm-key')
    page.locator('#save-weather').click()
    page.wait_for_function('config.weather.provider === "openweathermap"')
    assert saved[-1] == dict(weather=dict(provider='openweathermap', apiKey='fixture-owm-key'))
    assert page.locator('#weather-city-code-field').is_hidden() and page.locator('#weather-api-host-field').is_hidden()
    results['openweathermap_keyed_provider_saves'] = True

    page.locator('#weather-provider').select_option('amap')
    assert page.locator('#weather-city-code-field').is_visible() and page.locator('#weather-api-host-field').is_hidden()
    assert 'Web 服务 Key' in page.locator('#weather-provider-help').inner_text() and '不提供小时预报' in page.locator('#weather-provider-help').inner_text()
    assert '不是 JS' in page.locator('#weather-api-key').get_attribute('placeholder')
    page.locator('#weather-api-key').fill('fixture-amap-key')
    before = len(saved)
    for code in ('', '32050', '32050x'):
        page.locator('#weather-city-code').fill(code)
        page.locator('#save-weather').click()
        page.wait_for_function('document.getElementById("status").textContent.includes("6 位城市行政区代码")')
        assert len(saved) == before
    page.locator('#weather-city-code').fill('320500')
    page.locator('#save-weather').click()
    page.wait_for_function('config.weather.provider === "amap"')
    assert saved[-1] == dict(weather=dict(provider='amap', apiKey='fixture-amap-key', cityCode='320500'))
    assert page.locator('#weather-city-code').input_value() == '320500' and page.locator('#save-weather').is_disabled()
    page.locator('#weather-city-code').fill('310000')
    assert page.locator('#save-weather').is_enabled()
    page.locator('#quota-refresh-minutes').fill('11')
    page.locator('#save-quota-refresh').click()
    page.wait_for_function('config.quotaRefreshMinutes === 11')
    assert page.locator('#weather-city-code').input_value() == '310000' and page.locator('#save-weather').is_enabled()
    page.locator('#cancel-weather').click()
    assert page.locator('#weather-city-code').input_value() == '320500' and page.locator('#save-weather').is_disabled()
    page.reload(wait_until='networkidle')
    assert page.locator('#weather-city-code').input_value() == '320500' and page.locator('#weather-api-key').input_value() == ''
    results['amap_validates_code_restores_saved_fields_and_preserves_drafts'] = True

    page.locator('#weather-provider').select_option('qweather')
    assert page.locator('#weather-city-code').input_value() == '' and page.locator('#weather-city-code-field').is_hidden()
    assert page.locator('#weather-api-host-field').is_visible() and '坐标' in page.locator('#weather-provider-help').inner_text()
    page.locator('#weather-api-key').fill('fixture-qweather-key')
    before = len(saved)
    for host in ('', 'https://abc123.qweatherapi.com', 'abc123.qweatherapi.com/path', 'abc123.qweatherapi.com:443', 'abc123.qweatherapi.com.evil.test', 'localhost'):
        page.locator('#weather-api-host').fill(host)
        page.locator('#save-weather').click()
        page.wait_for_function('document.getElementById("status").textContent.includes("有效和风 API Host")')
        assert len(saved) == before
    page.locator('#weather-api-host').fill('ABC123.qweatherapi.com')
    page.locator('#save-weather').click()
    page.wait_for_function('config.weather.provider === "qweather"')
    assert saved[-1] == dict(weather=dict(provider='qweather', apiKey='fixture-qweather-key', apiHost='abc123.qweatherapi.com'))
    assert page.locator('#weather-api-host').input_value() == 'abc123.qweatherapi.com' and page.locator('#save-weather').is_disabled()
    page.locator('#weather-api-host').fill('draft.qweatherapi.com')
    page.locator('#quota-refresh-minutes').fill('12')
    page.locator('#save-quota-refresh').click()
    page.wait_for_function('config.quotaRefreshMinutes === 12')
    assert page.locator('#weather-api-host').input_value() == 'draft.qweatherapi.com' and page.locator('#save-weather').is_enabled()
    page.locator('#cancel-weather').click()
    assert page.locator('#weather-api-host').input_value() == 'abc123.qweatherapi.com'
    page.reload(wait_until='networkidle')
    assert page.locator('#weather-api-host').input_value() == 'abc123.qweatherapi.com' and page.locator('#weather-api-key').input_value() == ''
    page.locator('#weather-api-key').fill('another-unsaved-key')
    page.locator('#weather-provider').select_option('amap')
    assert page.locator('#weather-api-host').input_value() == '' and page.locator('#weather-api-key').input_value() == ''
    assert page.locator('#weather-city-code').input_value() == ''
    page.locator('#weather-provider').select_option('qweather')
    assert page.locator('#weather-api-host').input_value() == 'abc123.qweatherapi.com' and page.locator('#save-weather').is_disabled()
    for width in (1200, 390):
        page.set_viewport_size(dict(width=width, height=1000))
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.locator('#weather-section').screenshot(path=str(OUT / f'qweather-admin-{width}.png'))
    results['qweather_validates_host_preserves_drafts_and_never_carries_other_provider_fields'] = True

    before = len(saved)
    page.locator('#weather-api-host').fill('another.qweatherapi.com')
    assert 'API Host 已更改' in page.locator('#weather-key-status').inner_text()
    assert page.locator('#weather-clear-key').is_enabled()
    page.locator('#save-weather').click()
    page.wait_for_function('document.getElementById("status").textContent.includes("API Host 已更改")')
    assert len(saved) == before and cfg['weather']['apiHost'] == 'abc123.qweatherapi.com'
    page.locator('#weather-api-host').fill(' ABC123.qweatherapi.com ')
    assert '留空保留' in page.locator('#weather-key-status').inner_text() and page.locator('#save-weather').is_disabled()
    page.locator('#weather-api-host').fill('another.qweatherapi.com')
    page.locator('#weather-api-key').fill('fixture-new-host-key')
    page.locator('#weather-api-host').fill('target.qweatherapi.com')
    assert page.locator('#weather-api-key').input_value() == 'fixture-new-host-key'
    page.locator('#save-weather').click()
    page.wait_for_function('config.weather.apiHost === "target.qweatherapi.com"')
    assert saved[-1] == dict(weather=dict(provider='qweather', apiKey='fixture-new-host-key', apiHost='target.qweatherapi.com'))
    page.locator('#weather-api-host').fill('clear-target.qweatherapi.com')
    page.locator('#weather-clear-key').check()
    page.locator('#save-weather').click()
    page.wait_for_function('config.weather.apiHost === "clear-target.qweatherapi.com" && !config.weather.apiKeyConfigured')
    assert saved[-1] == dict(weather=dict(provider='qweather', clearApiKey=True, apiHost='clear-target.qweatherapi.com'))
    results['qweather_host_change_requires_new_key_or_explicit_clear'] = True

    assert page.locator('#weather-api-host').get_attribute('maxlength') == '253'
    page.locator('#weather-api-key').fill('fixture-label-boundary-key')
    before = len(saved)
    page.locator('#weather-api-host').evaluate('(node) => { node.value = "a".repeat(64) + ".qweatherapi.com"; node.dispatchEvent(new Event("input", { bubbles: true })); }')
    page.locator('#save-weather').click()
    page.wait_for_function('document.getElementById("status").textContent.includes("有效和风 API Host")')
    assert len(saved) == before
    host63 = 'a' * 63 + '.qweatherapi.com'
    page.locator('#weather-api-host').fill(host63)
    page.locator('#save-weather').click()
    page.wait_for_function('(host) => config.weather.apiHost === host', arg=host63)
    assert saved[-1] == dict(weather=dict(provider='qweather', apiKey='fixture-label-boundary-key', apiHost=host63))
    results['qweather_host_label_boundary_matches_backend'] = True

    page.locator('#weather-api-key').fill('fixture-regional-host-key')
    before = len(saved)
    for host in ('demo..re.qweatherapi.com', 'demo.-re.qweatherapi.com', 'demo.re-.qweatherapi.com', 'demo.r_e.qweatherapi.com',
                 'demo.地区.qweatherapi.com', 'demo.re.qweatherapi.com.evil.test', '192.168.1.1', 'qweatherapi.com',
                 'https://demo.re.qweatherapi.com', 'demo.re.qweatherapi.com:443', 'demo.re.qweatherapi.com/path',
                 'demo.' + 'r' * 64 + '.qweatherapi.com'):
        page.locator('#weather-api-host').fill(host)
        page.locator('#save-weather').click()
        page.wait_for_function('document.getElementById("status").textContent.includes("有效和风 API Host")')
        assert len(saved) == before
    page.locator('#weather-api-host').fill('DEMO.re.qweatherapi.com')
    page.locator('#save-weather').click()
    page.wait_for_function('config.weather.apiHost === "demo.re.qweatherapi.com"')
    assert saved[-1] == dict(weather=dict(provider='qweather', apiKey='fixture-regional-host-key', apiHost='demo.re.qweatherapi.com'))
    page.reload(wait_until='networkidle')
    assert page.locator('#weather-api-host').input_value() == 'demo.re.qweatherapi.com' and page.locator('#save-weather').is_disabled()
    results['qweather_regional_host_saves_and_rejects_invalid_dns_labels'] = True

    host253 = '.'.join(['a' * 63, 'b' * 63, 'c' * 63, 'd' * 45]) + '.qweatherapi.com'
    assert len(host253) == 253
    page.locator('#weather-api-key').fill('fixture-total-length-key')
    before = len(saved)
    page.locator('#weather-api-host').evaluate('(node, host) => { node.value = host; node.dispatchEvent(new Event("input", { bubbles: true })); }', host253.replace('d' * 45, 'd' * 46))
    page.locator('#save-weather').click()
    page.wait_for_function('document.getElementById("status").textContent.includes("有效和风 API Host")')
    assert len(saved) == before
    page.locator('#weather-api-host').fill(host253)
    page.locator('#save-weather').click()
    page.wait_for_function('(host) => config.weather.apiHost === host', arg=host253)
    assert saved[-1] == dict(weather=dict(provider='qweather', apiKey='fixture-total-length-key', apiHost=host253))
    results['qweather_total_host_length_253_accepted_254_rejected'] = True

    page.locator('#weather-provider').select_option('open-meteo')
    page.locator('#save-weather').click()
    page.wait_for_function('config.weather.provider === "open-meteo"')
    assert saved[-1] == dict(weather=dict(provider='open-meteo'))
    assert cfg['weather']['apiHost'] is None and cfg['weather']['cityCode'] is None
    results['six_providers_switch_without_leaking_provider_configuration'] = True

    # Older bridge configurations still initialize this editor without making a write.
    cfg['clock'] = None
    cfg.pop('weather')
    before = len(saved)
    page.reload(wait_until='networkidle')
    assert page.locator('#clock-timezone').input_value() == 'Asia/Shanghai'
    assert page.locator('#weather-provider').input_value() == 'open-meteo'
    assert page.locator('#save-clock').is_enabled() and page.locator('#save-weather').is_disabled() and len(saved) == before
    assert '尚未启用' in page.locator('#clock-mode-status').inner_text()
    page.locator('#save-clock').click()
    page.wait_for_function('config.clock !== null')
    assert saved[-1] == dict(clock=clock) and page.locator('#save-clock').is_disabled()
    results['legacy_config_can_enable_default_clock_explicitly'] = True

    for width in (1200, 390):
        page.set_viewport_size(dict(width=width, height=1000))
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.locator('#clock-section').screenshot(path=str(OUT / f'clock-admin-{width}.png'))
        page.locator('#weather-section').screenshot(path=str(OUT / f'weather-admin-{width}.png'))
    assert not errors, errors
    results['desktop_mobile_layout_and_no_browser_errors'] = True
    browser.close()
server.shutdown()
(OUT / 'clock-weather-admin-result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Clock/weather admin: {len(results)} checks passed; fixtures only; no browser errors.')
