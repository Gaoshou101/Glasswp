"""Print a compact review view; full text remains in the local snapshot."""
import json
from pathlib import Path
import re
import sys

root = Path(__file__).resolve().parent
pages = json.loads((root / 'sources.json').read_text(encoding='utf-8'))['pages']
group = sys.argv[1]
start = int(sys.argv[2]) if len(sys.argv) > 2 else 0
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 20
selected = [r for r in pages if ('/scene/' in r['url'] and '/scenescript/' not in r['url'])] if group == 'scene' else [r for r in pages if '/scenescript/' in r['url']] if group == 'script' else [r for r in pages if 'help.wallpaperengine.io' in r['url']]
for r in selected[start:start + limit]:
    text = (root / r['file']).read_text(encoding='utf-8')
    compact = re.sub(r'\s+', ' ', text)
    print('\nSOURCE:', r['url'])
    print(compact[:1100])
    warnings = re.findall(r'(?:Important|WARNING|Please note|Please Note|TIP|Note on)[\s\S]{0,800}', text)
    for warning in warnings[:2]:
        print('DETAIL:', re.sub(r'\s+', ' ', warning))
    if group == 'script':
        lines = text.splitlines()
        signatures = [line for line in lines if re.search(r'\w+\(.*\)|^\w+: \w', line)]
        print('SIGNATURES:', '; '.join(dict.fromkeys(signatures)))
print('\nGroup total:', len(selected), 'Read slice:', start, start + limit)
