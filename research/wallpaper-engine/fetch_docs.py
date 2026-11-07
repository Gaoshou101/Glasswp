"""Snapshot official English documentation, retaining sources and coverage evidence."""
import concurrent.futures
import datetime
import hashlib
import json
from pathlib import Path
import time
import xml.etree.ElementTree as ET

import requests
from bs4 import BeautifulSoup

ROOT = Path(__file__).resolve().parent
RAW = ROOT / 'raw'
RAW.mkdir(parents=True, exist_ok=True)
SITES = ['https://docs.wallpaperengine.io', 'https://help.wallpaperengine.io']

def fetch(url):
    for attempt in range(3):
        try:
            response = requests.get(url, timeout=40)
            response.raise_for_status()
            soup = BeautifulSoup(response.content, 'html.parser')
            article = soup.select_one('.theme-default-content') or soup.select_one('main')
            if not article:
                raise ValueError('No article body found')
            for tag in article.select('script, style, .header-anchor'):
                tag.decompose()
            text = article.get_text('\n', strip=True)
            name = hashlib.sha256(url.encode()).hexdigest()[:16] + '.txt'
            (RAW / name).write_text(text, encoding='utf-8')
            return {'url': url, 'title': soup.title.get_text() if soup.title else '',
                    'file': 'raw/' + name, 'chars': len(text), 'status': response.status_code,
                    'sha256': hashlib.sha256(text.encode()).hexdigest()}
        except Exception as exc:
            error = str(exc)
            time.sleep(attempt + 1)
    return {'url': url, 'error': error}

def main():
    urls = set()
    for site in SITES:
        response = requests.get(site + '/sitemap.xml', timeout=40)
        response.raise_for_status()
        tree = ET.fromstring(response.content)
        urls.update(node.text for node in tree.findall('.//{*}loc') if '/en/' in node.text)
    records = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
        for record in executor.map(fetch, sorted(urls)):
            records.append(record)
            if len(records) % 40 == 0:
                print(f'{len(records)}/{len(urls)} fetched', flush=True)
    manifest = {'retrieved_at_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                'scope': 'All English pages in both official sitemaps; includes mobile appendices',
                'total': len(records), 'success': sum('error' not in r for r in records), 'pages': records}
    extra = []
    for url, name in [('https://www.wallpaperengine.io/en', 'product-homepage.txt'),
                      ('https://docs.wallpaperengine.io/reference/lib.sceneScript.d.ts', 'lib.sceneScript.d.ts')]:
        response = requests.get(url, timeout=40)
        response.raise_for_status()
        if name.endswith('.d.ts'):
            content = response.content
        else:
            content = BeautifulSoup(response.content, 'html.parser').get_text('\n', strip=True).encode('utf-8')
        (RAW / name).write_bytes(content)
        extra.append({'url': url, 'file': 'raw/' + name, 'bytes': len(content),
                      'sha256': hashlib.sha256(content).hexdigest()})
    manifest['additional_sources'] = extra
    (ROOT / 'sources.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    groups = {'web': [], 'script': [], 'scene': [], 'help': []}
    for r in records:
        if 'error' in r:
            continue
        u = r['url']
        group = 'help' if 'help.wallpaperengine' in u else 'web' if '/web/' in u else 'script' if '/scenescript/' in u else 'scene'
        groups[group].append('\n\nSOURCE: ' + u + '\n' + (ROOT / r['file']).read_text(encoding='utf-8'))
    for group, texts in groups.items():
        (ROOT / ('combined-' + group + '.txt')).write_text(''.join(texts), encoding='utf-8')
    index = ['# Wallpaper Engine 官方资料索引', '',
             '抓取时间（UTC）：' + manifest['retrieved_at_utc'], '',
             f'正文获取：{manifest["success"]}/{manifest["total"]}。获取成功不等于所有段落及媒体已精读。', '',
             '研究总结：../../docs/WALLPAPER-ENGINE-RESEARCH.md', '',
             '原文位于本地 raw/；未纳入 Git。页面哈希和错误记录见 sources.json。', '']
    for site in SITES:
        index.extend(['## ' + site, ''])
        for record in records:
            if record['url'].startswith(site):
                label = record.get('title', record['url']).replace('|', ' / ')
                index.append('- [' + label + '](' + record['url'] + ') — ' + record.get('file', 'ERROR'))
        index.append('')
    index.extend(['## 补充来源', ''])
    index.extend('- [' + r['url'] + '](' + r['url'] + ') — ' + r['file'] for r in extra)
    (ROOT / 'SOURCES.md').write_text('\n'.join(index) + '\n', encoding='utf-8')
    print(json.dumps({k: manifest[k] for k in ['total', 'success']}), flush=True)
    print(json.dumps({k: sum(map(len, v)) for k, v in groups.items()}), flush=True)

if __name__ == '__main__':
    main()
