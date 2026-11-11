"""Verify the three publication packages without launching Steam or reading private data."""
from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[1]
EXPECTED = {
    'Glasswp-Purple': ('original', 'assets/wallpaper-original.jpg'),
    'Glasswp-Angel': ('neon', 'assets/wallpapers/neon.jpg'),
    'Glasswp-Corgi': ('pool', 'assets/wallpapers/pool.jpg'),
}

def publication_ids(value):
    if isinstance(value, dict):
        return any(re.search(r'workshop.*id|published.*file.*id', key, re.I) or publication_ids(child) for key, child in value.items())
    if isinstance(value, list):
        return any(publication_ids(child) for child in value)
    return False

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

for title, (preset, background) in EXPECTED.items():
    folder = ROOT / 'artifacts' / title
    project = json.loads((folder / 'project.json').read_text(encoding='utf-8'))
    info = json.loads((folder / 'package-info.json').read_text(encoding='utf-8'))
    assert project['title'] == title
    assert project['file'] == 'index.html'
    assert project['preview'] == 'preview.jpg'
    assert project['visibility'] == 'private'
    assert not publication_ids(project), f'{title} includes a Workshop publication ID'
    assert project['general']['properties']['bridge_token']['value'] == ''
    html = (folder / 'index.html').read_text(encoding='utf-8')
    assert re.search(r'<html\b[^>]*data-wallpaper-preset="' + preset + r'"', html)
    assert '__GLASSWP_PIN_THEME' not in html
    assert background in html
    assert info['preset'] == preset and info['background'] == background
    preview = (folder / project['preview']).read_bytes()
    assert preview.startswith(b'\xff\xd8\xff'), f'{title} preview extension does not match JPEG bytes'
    assert preview == (ROOT / 'dist' / background).read_bytes(), f'{title} preview must contain only source artwork'
    assert preview == (ROOT / 'public' / background).read_bytes(), f'{title} artwork differs from public source'
    assert (folder / 'theme.js').read_bytes() == (ROOT / 'public/theme.js').read_bytes()
    assert (folder / 'wallpaper.js').read_bytes() == (ROOT / 'dist' / 'wallpaper.js').read_bytes()
    assert sha(folder / 'assets/wallpaper-original.jpg') == sha(ROOT / 'dist/assets/wallpaper-original.jpg')
    assert sha(folder / 'assets/wallpaper-original.jpg') == sha(ROOT / 'public/assets/wallpaper-original.jpg')
    actual = {p.relative_to(folder).as_posix(): sha(p) for p in folder.rglob('*') if p.is_file() and p.name != 'package-info.json'}
    assert actual == info['files'], f'{title} package manifest mismatch'
    assert not any(p.is_symlink() for p in folder.rglob('*'))
    instructions = (folder / '使用说明.txt').read_text(encoding='utf-8')
    assert all(text in instructions for text in ['运行步骤', '手动发布', '背景来源及权利', '展示票据'])
    print(f'{title}: preset={preset}; files={len(actual) + 1}; empty ticket; no Workshop ID; artwork preview; hashes verified')
print('All three Workshop packages passed.')
