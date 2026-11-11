"""Isolated bridge + anonymous Magpie server; never reads real login or changes installed configuration."""
import http.server
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts' / 'validation'
OUT.mkdir(parents=True, exist_ok=True)
data = Path(tempfile.mkdtemp(prefix='ai-api-', dir=OUT))
calls, managed_calls = [], []


class Magpie(http.server.BaseHTTPRequestHandler):
    def send_json(self, payload):
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        assert self.path == '/v0/management/api-call'
        assert self.headers.get('Authorization') == 'Bearer cpa-fixture-key'
        payload = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        assert payload['url'] == 'https://chatgpt.com/backend-api/wham/usage'
        managed_calls.append(self.path)
        self.send_json(dict(status_code=200, body=json.dumps(dict(plan_type='plus', rate_limit=dict(primary_window=dict(limit_window_seconds=18000, used_percent=30))))))

    def do_GET(self):
        if self.path == '/v0/management/auth-files':
            assert self.headers.get('Authorization') == 'Bearer cpa-fixture-key'
            managed_calls.append(self.path)
            self.send_json(dict(files=[dict(name='private-login.json', provider='codex', auth_index='cpa-index', id_token=dict(chatgpt_account_id='private-workspace'))]))
            return
        if self.path.startswith('/api/v1/admin/accounts?'):
            assert self.headers.get('x-api-key') == 'sub2api-fixture-key' and self.headers.get('Authorization') is None
            managed_calls.append(self.path)
            self.send_json(dict(code=0, data=dict(total=1, items=[dict(id=1, name='Sub2API account', platform='openai', type='oauth')])))
            return
        if self.path == '/api/v1/admin/openai/accounts/1/quota':
            assert self.headers.get('x-api-key') == 'sub2api-fixture-key'
            managed_calls.append(self.path)
            self.send_json(dict(code=0, data=dict(plan_type='plus', fetched_at=int(time.time()), rate_limit=dict(primary_window=dict(limit_window_seconds=18000, used_percent=45)))))
            return
        calls.append((self.path, self.headers.get('Authorization')))
        assert self.path == '/v1/magpie/quotas?kept=1'
        body = json.dumps({'object': 'list', 'data': [dict(provider='codex', name='anonymous-fixture', user='hidden-user@example.test',
                                kind='subscription', plan='plus', readAt='2026-11-10T00:00:00Z',
                                windows=[dict(name='5h', remaining=25)])]}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


magpie = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Magpie)
threading.Thread(target=magpie.serve_forever, daemon=True).start()
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
base = f'http://127.0.0.1:{port}'
env = dict(os.environ, CODEX_HOME=str(data / 'codex'), CLAUDE_CONFIG_DIR=str(data / 'claude'))
process = subprocess.Popen([sys.argv[1], str(ROOT / 'bridge/WallpaperBridge/bin/Debug/net10.0-windows/WallpaperBridge.dll'),
                            '--port', str(port), '--data-dir', str(data / 'private'), '--web-root', str(ROOT / 'dist')],
                           cwd=ROOT, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
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


def wait_for(predicate):
    for _ in range(100):
        if predicate():
            return
        time.sleep(.1)
    raise AssertionError('source update timed out')


try:
    for _ in range(100):
        if process.poll() is not None:
            raise RuntimeError('bridge failed to start')
        try:
            if call('/v1/health')[0] == 200:
                break
        except OSError:
            time.sleep(.1)
    assert call('/v1/setup/ai-sources/status', {})[0] == 403
    assert call('/v1/setup/ai-sources/refresh', {})[0] == 403
    assert call('/v1/setup/ai-sources/refresh-status', {})[0] == 403
    session = call('/v1/setup/session', {}, {'X-Wallpaper-Setup': '1'})[1]['session']
    cfg = call('/v1/setup/config/read', {})[1]
    assert cfg['aiSources']['source'] == 'cpamp'
    assert not calls, 'default CPAMP must not query Magpie or scan native logins'
    read = {'X-Wallpaper-Token': cfg['readToken']}
    settings = dict(source='magpie',
                    magpieUrl=f'http://127.0.0.1:{magpie.server_port}', magpieApiKey='fixture-key')
    assert call('/v1/setup/config', {'aiSources': settings})[0] == 200
    wait_for(lambda: len(call('/v1/snapshot', headers=read)[1]['quota']['accounts']) == 1)
    snapshot = call('/v1/snapshot', headers=read)[1]
    assert snapshot['quota']['accounts'][0]['plan'] == 'GPT Plus'
    assert snapshot['quota']['accounts'][0]['windows'][0]['remainingPercent'] == 25
    public = json.dumps(snapshot) + json.dumps(call('/v1/setup/config/read', {})[1])
    assert 'fixture-key' not in public and 'hidden-user@example.test' not in public
    assert calls == [('/v1/magpie/quotas?kept=1', 'Bearer fixture-key')]
    assert call('/v1/setup/ai-sources/status', {})[1]['accounts'][0]['id'] == snapshot['quota']['accounts'][0]['id']
    account_id = snapshot['quota']['accounts'][0]['id']
    assert cfg['aiSources']['localOptions']['refreshMinutes'] == 10
    assert cfg['aiSources']['magpieOptions']['refreshMinutes'] == 1
    options = dict(autoRefreshEnabled=False, refreshMinutes=7, accounts=[])
    assert call('/v1/setup/config', {'aiSources': {'magpieOptions': options}})[0] == 200
    assert call('/v1/snapshot', headers=read)[1]['quota']['accounts'] == []
    assert len(call('/v1/setup/ai-sources/status', {})[1]['accounts']) == 1
    options['accounts'] = [dict(id=account_id, alias='My account')]
    assert call('/v1/setup/config', {'aiSources': {'magpieOptions': options}})[0] == 200
    assert call('/v1/snapshot', headers=read)[1]['quota']['accounts'][0]['alias'] == 'My account'
    assert len(calls) == 1, 'display settings must not cause extra upstream reads'
    configured = call('/v1/setup/config/read', {})[1]['aiSources']
    assert configured['magpieOptions'] == options and configured['localOptions']['accounts'] is None
    assert not call('/v1/setup/ai-sources/refresh-status', {})[1]['enabled']
    for invalid in ({**options, 'refreshMinutes': 0}, {**options, 'accounts': [dict(id=account_id), dict(id=account_id)]}):
        assert call('/v1/setup/config', {'aiSources': {'magpieOptions': invalid}})[0] == 400
        assert call('/v1/setup/config/read', {})[1]['aiSources']['magpieOptions'] == options
    assert call(f'/v1/accounts/{account_id}/activity', headers=read)[1]['status'] == 'unsupported'
    assert call('/v1/quota-refresh/status', headers=read)[1]['canRefresh']
    assert call('/v1/quota-refresh', {}, read)[0] == 200
    for _ in range(3):
        assert call('/v1/setup/ai-sources/refresh', {})[0] == 200
    time.sleep(1)
    assert len(calls) == 1, 'manual requests must respect the per-source cooldown'
    for source in ('', 'CPAMP', 'unknown', 'local,magpie'):
        assert call('/v1/setup/config', {'aiSources': {'source': source}})[0] == 400
        assert call('/v1/setup/config/read', {})[1]['aiSources']['source'] == 'magpie'
        assert len(call('/v1/snapshot', headers=read)[1]['quota']['accounts']) == 1
    assert call('/v1/setup/config', {'aiSources': {}})[0] == 200
    retained = call('/v1/setup/config/read', {})[1]['aiSources']
    assert retained['source'] == 'magpie' and retained['magpieKeyConfigured'] and retained['magpieUrl'] == settings['magpieUrl']
    assert call('/v1/setup/config', {'clearManagementKey': True})[0] == 200
    assert len(call('/v1/snapshot', headers=read)[1]['quota']['accounts']) == 1
    for url in ('http://remote.example', 'https://example.test/?key=secret', 'https://user:pass@example.test'):
        assert call('/v1/setup/config', {'aiSources': {**settings, 'magpieUrl': url}})[0] == 400
    assert call('/v1/setup/config', {'aiSources': {'source': 'cpamp'}})[0] == 200
    assert call('/v1/snapshot', headers=read)[1]['quota']['accounts'] == []
    assert call('/v1/setup/ai-sources/status', {})[1]['accounts'] == []
    assert not call('/v1/quota-refresh/status', headers=read)[1]['canRefresh']
    retained = call('/v1/setup/config/read', {})[1]['aiSources']
    assert retained['source'] == 'cpamp' and retained['magpieUrl'] == settings['magpieUrl'] and retained['magpieKeyConfigured']
    assert call('/v1/setup/ai-sources/refresh', {})[0] == 200
    time.sleep(1)
    assert len(calls) == 1, 'unselected Magpie must not be queried by refresh requests'
    assert call('/v1/setup/config', {'aiSources': {'source': 'magpie', 'magpieUrl': 'https://example.test'}})[0] == 200
    assert call('/v1/snapshot', headers=read)[1]['quota']['accounts'] == [], 'new origin cannot expose old source cards'
    assert not call('/v1/setup/config/read', {})[1]['aiSources']['magpieKeyConfigured']
    assert call('/v1/setup/config', {'aiSources': {'source': 'cpamp'}})[0] == 200
    assert call('/v1/setup/config/read', {})[1]['accounts'] == []
    endpoint = f'http://127.0.0.1:{magpie.server_port}'
    for source, expected in [('cpa', 70), ('sub2api', 55)]:
        assert call('/v1/setup/config', {'aiSources': {'source': source, source + 'Url': endpoint, source + 'Key': source + '-fixture-key'}})[0] == 200
        wait_for(lambda: any(a['id'].startswith(source + ':') for a in call('/v1/snapshot', headers=read)[1]['quota']['accounts']))
        account = call('/v1/snapshot', headers=read)[1]['quota']['accounts'][0]
        assert account['plan'] == 'GPT Plus' and account['windows'][0]['remainingPercent'] == expected
        assert len(call('/v1/setup/ai-sources/status', {})[1]['accounts']) == 1
        config = call('/v1/setup/config/read', {})[1]
        assert config['aiSources'][source + 'KeyConfigured'] and source + 'Key' not in config['aiSources']
        assert '-fixture-key' not in json.dumps(config)
        count = len(managed_calls)
        assert call('/v1/setup/config', {'aiSources': {source + 'Options': dict(autoRefreshEnabled=False, refreshMinutes=19, accounts=[])}})[0] == 200
        assert call('/v1/snapshot', headers=read)[1]['quota']['accounts'] == []
        assert len(call('/v1/setup/ai-sources/status', {})[1]['accounts']) == 1
        assert call('/v1/setup/ai-sources/refresh', {})[0] == 200
        time.sleep(.7)
        assert len(managed_calls) == count
    assert len(managed_calls) == 4 and len(calls) == 1
    final_config = call('/v1/setup/config/read', {})[1]['aiSources']
    assert final_config['cpaKeyConfigured'] and final_config['sub2apiKeyConfigured']
    assert call('/v1/setup/config', {'aiSources': {'sub2apiUrl': 'https://new.example.test'}})[0] == 200
    changed = call('/v1/setup/config/read', {})[1]['aiSources']
    assert not changed['sub2apiKeyConfigured'] and changed['cpaKeyConfigured'] and changed['sub2apiOptions']['accounts'] is None
    assert call('/v1/snapshot', headers=read)[1]['quota']['accounts'] == []
    print('AI source API checks passed: exclusive selection, authorization, secrets, quota, activity, cooldown, retained credentials, invalid source and origin switch; CPA/Sub2API end-to-end quota, secret isolation, display filters and cooldown')
finally:
    process.terminate()
    process.wait(timeout=15)
    magpie.shutdown()
