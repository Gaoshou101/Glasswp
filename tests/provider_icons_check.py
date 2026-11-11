"""Load the complete packaged icon catalog in Chromium, offline and at two densities."""
import json
import sys
import subprocess
from pathlib import Path
from html import escape

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/python'))
from playwright.sync_api import sync_playwright

OUT = ROOT / 'artifacts/validation/provider-icons'
OUT.mkdir(parents=True, exist_ok=True)
catalog = json.loads(subprocess.check_output([
    'node', '--import', 'tsx', '--input-type=module', '-e',
    "import {providerLogoPaths,providerNames} from './src/icons.ts'; console.log(JSON.stringify(Object.entries(providerLogoPaths).map(([brand,path])=>({brand,path,name:providerNames[brand]||brand}))));"
], cwd=ROOT, text=True, encoding='utf8'))
cards = ''.join(f'<article><span class="account-mark" data-brand="{escape(item["brand"])}"><img src="{(ROOT / "dist" / item["path"]).as_uri()}" alt=""></span><span>{escape(item["name"])}</span></article>' for item in catalog)
html = '<!doctype html><html lang="zh-CN"><meta charset="utf-8">' + f'<link rel="stylesheet" href="{(ROOT / "dist/wallpaper.css").as_uri()}">' + '''<style>
body{margin:0;padding:32px;background:#12101c;color:#f6f3ff;font:14px 'Segoe UI','Microsoft YaHei',sans-serif}
h1{font-size:22px;margin:0 0 10px}p{color:#bdb7ca;margin:0 0 24px}
.wallpaper{--u:1px;--accent-rgb:180,160,220;position:static;width:auto;height:auto;aspect-ratio:auto;display:grid;grid-template-columns:repeat(6,1fr);gap:12px;background:none;overflow:visible;transform:none}
article{display:flex;align-items:center;gap:10px;min-width:0;padding:16px 12px;background:#24202f;border-radius:12px}
article>span:last-child{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
</style><h1>AI Agent · 官方本地图标库</h1><p>28 × 28 底板 · 22 × 22 内容区 · 保留品牌原色</p><main class="wallpaper">''' + cards + '</main></html>'
(OUT / 'gallery.html').write_text(html, encoding='utf8')
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=r'C:\Program Files\Google\Chrome\Application\chrome.exe')
    for density in [1, 2]:
        context = browser.new_context(viewport=dict(width=1400, height=1000), device_scale_factor=density)
        page = context.new_page()
        requests = []
        page.route('http://**/*', lambda route: (requests.append(route.request.url), route.abort()))
        page.route('https://**/*', lambda route: (requests.append(route.request.url), route.abort()))
        page.goto((OUT / 'gallery.html').as_uri(), wait_until='networkidle')
        broken = page.locator('img').evaluate_all('nodes=>nodes.filter(n=>!n.complete||n.naturalWidth===0).map(n=>n.src)')
        assert not broken, broken
        assert not requests, requests
        assert page.locator('.account-mark').evaluate_all('nodes=>nodes.every(n=>n.getBoundingClientRect().width===28&&n.getBoundingClientRect().height===28)')
        assert page.locator('img').evaluate_all('nodes=>nodes.every(n=>n.getBoundingClientRect().width===22&&n.getBoundingClientRect().height===22&&getComputedStyle(n).objectFit==="contain")')
        page.screenshot(path=str(OUT / f'gallery-{density}x.png'), full_page=True)
        page.locator('body').evaluate("node=>{node.style.background='#ece9f0';node.style.color='#17131e'}")
        page.screenshot(path=str(OUT / f'gallery-light-{density}x.png'), full_page=True)
        context.close()
    browser.close()
print(f'Offline browser icon check passed: {len(catalog)} brands, 1x/2x, 28px/22px, no broken images or remote requests')
