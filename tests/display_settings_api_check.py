"""Exercise a separate bridge with disposable configuration; never touches the installed bridge."""
import json
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts' / 'validation'
OUT.mkdir(parents=True, exist_ok=True)
data = Path(tempfile.mkdtemp(prefix='display-api-', dir=OUT))
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
base = f'http://127.0.0.1:{port}'
dotnet = sys.argv[1]
process = subprocess.Popen([dotnet, str(ROOT / 'bridge/WallpaperBridge/bin/Debug/net10.0-windows/WallpaperBridge.dll'),
                            '--port', str(port), '--data-dir', str(data), '--web-root', str(ROOT / 'dist')],
                           cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           creationflags=subprocess.CREATE_NO_WINDOW)
session = None


def call(path, body=None, headers=None):
    hdr = {'Origin': base, **(headers or {})}
    if session:
        hdr['X-Wallpaper-Admin'] = session
    if body is not None:
        hdr['Content-Type'] = 'application/json'
    req = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=hdr)
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


try:
    for _ in range(100):
        if process.poll() is not None:
            raise RuntimeError('Isolated bridge failed to start')
        try:
            if call('/v1/health')[0] == 200:
                break
        except (OSError, urllib.error.URLError):
            time.sleep(.1)
    else:
        raise RuntimeError('Isolated bridge startup timed out')
    assert call('/v1/setup/config/read', {})[0] == 403
    assert call('/v1/setup/time/sync', {})[0] == 403
    status, payload = call('/v1/setup/session', {}, {'X-Wallpaper-Setup': '1'})
    assert status == 200
    session = payload['session']
    status, cfg = call('/v1/setup/config/read', {})
    assert status == 200 and cfg['clock'] is None
    assert cfg['weather'] == dict(provider='open-meteo', apiKeyConfigured=False, apiHost=None, cityCode=None)
    if '--live-sync' in sys.argv:
        status, measured = call('/v1/setup/time/sync', {})
        assert status == 200, (status, measured)
        assert abs(measured['offsetSeconds']) <= 86400 and measured['roundTripMs'] >= 0
        assert call('/v1/setup/config/read', {})[1]['clock'] is None, 'Measuring time must not save configuration'
        print('Live HTTPS time measurement succeeded; configuration unchanged')
    read_headers = {'X-Wallpaper-Token': cfg['readToken']}
    clock = dict(timezone='America/New_York', offsetSeconds=12.125, seconds=True, hour12=True,
                 worldZones=['Asia/Shanghai', 'Europe/London', 'America/New_York', 'Asia/Kathmandu'])
    assert call('/v1/setup/config', {'clock': clock})[0] == 200
    status, snapshot = call('/v1/snapshot', headers=read_headers)
    assert status == 200 and snapshot['clock'] == clock
    assert 'apiKey' not in json.dumps(snapshot)
    for patch in ({'timezone': 'Invalid/Zone'}, {'offsetSeconds': 86401}, {'worldZones': ['UTC', 'UTC']}, {'worldZones': None}):
        assert call('/v1/setup/config', {'clock': {**clock, **patch}})[0] == 400
        assert call('/v1/setup/config/read', {})[1]['clock'] == clock
    assert call('/v1/setup/config', {'weather': {'provider': 'weatherapi', 'apiKey': 'fixture-only-secret'}})[0] == 200
    saved = call('/v1/setup/config/read', {})[1]
    assert saved['weather'] == dict(provider='weatherapi', apiKeyConfigured=True, apiHost=None, cityCode=None)
    assert 'fixture-only-secret' not in json.dumps(saved)
    assert call('/v1/setup/config', {'weather': {'provider': 'weatherapi', 'apiKey': ''}})[0] == 200
    assert call('/v1/setup/config/read', {})[1]['weather']['apiKeyConfigured']
    assert call('/v1/setup/config', {'weather': {'provider': 'openweathermap'}})[0] == 400
    assert call('/v1/setup/config/read', {})[1]['weather']['provider'] == 'weatherapi'
    assert call('/v1/setup/config', {'weather': {'provider': 'weatherapi', 'clearApiKey': True}})[0] == 200
    assert not call('/v1/setup/config/read', {})[1]['weather']['apiKeyConfigured']
    # Use explicitly cleared credentials for these integration checks: no upstream request is made.
    assert call('/v1/setup/config', {'weather': {'provider': 'amap', 'cityCode': '320500', 'clearApiKey': True}})[0] == 200
    saved = call('/v1/setup/config/read', {})[1]
    assert saved['weather'] == dict(provider='amap', apiKeyConfigured=False, apiHost=None, cityCode='320500')
    for code in ('32050', '苏州', '1234567'):
        assert call('/v1/setup/config', {'weather': {'provider': 'amap', 'cityCode': code, 'clearApiKey': True}})[0] == 400
    assert call('/v1/setup/config', {'weather': {'provider': 'qweather', 'apiHost': 'fixture.re.qweatherapi.com', 'clearApiKey': True}})[0] == 200
    saved = call('/v1/setup/config/read', {})[1]
    assert saved['weather'] == dict(provider='qweather', apiKeyConfigured=False, apiHost='fixture.re.qweatherapi.com', cityCode=None)
    for host in ('https://fixture.qweatherapi.com', 'localhost', 'fixture.qweatherapi.com.evil.test', 'fixture.qweatherapi.com:443'):
        assert call('/v1/setup/config', {'weather': {'provider': 'qweather', 'apiHost': host, 'clearApiKey': True}})[0] == 400
    assert call('/v1/setup/config', {'weather': {'provider': 'met-no'}})[0] == 200
    saved = call('/v1/setup/config/read', {})[1]
    assert saved['weather'] == dict(provider='met-no', apiKeyConfigured=False, apiHost=None, cityCode=None)
    assert saved['clock'] == clock
    # A fresh store instance must restore the encrypted clock/provider configuration.
    process.terminate()
    process.wait(timeout=10)
    process = subprocess.Popen([dotnet, str(ROOT / 'bridge/WallpaperBridge/bin/Debug/net10.0-windows/WallpaperBridge.dll'),
                                '--port', str(port), '--data-dir', str(data)], cwd=ROOT,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
    session = None
    for _ in range(100):
        try:
            if call('/v1/health')[0] == 200:
                break
        except (OSError, urllib.error.URLError):
            time.sleep(.1)
    session = call('/v1/setup/session', {}, {'X-Wallpaper-Setup': '1'})[1]['session']
    saved = call('/v1/setup/config/read', {})[1]
    assert saved['clock'] == clock and saved['weather']['provider'] == 'met-no'
    report = {'authorization': True, 'clock_roundtrip': True, 'invalid_configuration_rejected': True,
              'weather_keys_redacted': True, 'provider_keys_isolated': True, 'encrypted_restart_persistence': True}
    (OUT / 'display-settings-api.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report))
finally:
    if process.poll() is None:
        process.terminate()
        process.wait(timeout=10)
