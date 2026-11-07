"""Create a navigation index from the fetched official SceneScript references."""
import json
from pathlib import Path
import re

root = Path(__file__).resolve().parent
manifest = json.loads((root / 'sources.json').read_text(encoding='utf-8'))
lines = ['# SceneScript 公开接口导航', '',
         '来源：官方 reference/class、reference/event 和 reference/module 页面。', '',
         '这是从文档提取的导航清单，不是经过运行验证的完整 SDK 或严格类型定义。', '',
         '[官方类型定义](https://docs.wallpaperengine.io/reference/lib.sceneScript.d.ts)', '',
         '版本差异与实现注意事项见 [研究报告](../../docs/WALLPAPER-ENGINE-RESEARCH.md)。', '']
for record in manifest['pages']:
    if '/scenescript/reference/' not in record['url']:
        continue
    text = (root / record['file']).read_text(encoding='utf-8')
    name = record['url'].split('/')[-1].replace('.html', '')
    lines.extend(['## ' + name, '', '[官方页面](' + record['url'] + ')', ''])
    signatures = []
    for line in text.splitlines():
        if re.match(r'^(?:static )?\w+\([^\n]*\)(?::.*)?;?$', line) or re.match(r'^\w+: (?:String|Number|Boolean|Vec\d|Mat\d|Float32Array|I\w+|Object|Array|float)', line):
            if line not in signatures:
                signatures.append(line)
    if signatures:
        lines.extend(['```text', *signatures, '```', ''])
    else:
        lines.extend([re.sub(r'\s+', ' ', text)[:700], ''])
(root / 'SCENESCRIPT-API-INDEX.md').write_text('\n'.join(lines), encoding='utf-8')
print('SceneScript API navigation generated.')
