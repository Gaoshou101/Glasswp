"""Build a compact source index and validate report item links."""
from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parent
data = json.loads((ROOT / 'sources.json').read_text(encoding='utf-8'))
rows = ['# 创作者研究来源索引', '',
        f"索引生成依据时间（UTC）：{data['retrieved_at_utc']}。缓存跨多次请求取得，非逐页独立时间戳。",
        '', '公开目录 12 页，355 个唯一条目；详情 22 个。原始资料在本地 raw/，不提交到 Git。',
        '', '## 目录', '']
for c in data['catalogs']:
    rows.append(f"- [{c['profile']} / 第 {c['page']} 页]({c['url']}) — {len(c['items'])} 条")
rows += ['', '## 代表作品与关联素材', '', '| ID / 页面 | 作者 | 分类 | 视频入口（未标明观看者仅收集链接） |', '| --- | --- | --- | --- |']
for d in data['details']:
    authors = '、'.join(a['name'] for a in d['authors'])
    tags = '; '.join(d['tags']).replace('|', '\\|')
    videos = ' '.join(f'[YouTube](https://www.youtube.com/watch?v={v})' for v in d['youtube_ids']) or '无页面视频 ID'
    title = d['title'].replace('|','\\|')
    rows.append(f"| [{d['id']} · {title}]({d['url']}) | {authors} | {tags} | {videos} |")
rows += ['', '## 浏览器直接观察', '',
         '静态预览：3470764447、3448290956、3599498791、3709882938、3673609113、3573886911、2970648387、3773747576。',
         '', 'Dock2 所链接 YouTube 视频 JHe0GYENWxQ：成功播放，抽样约 10.44、26.43、42.44 秒，未完整连续观看。',
         '', '浏览器页面状态见 preview-observations.json；视频播放状态见 video-check.json。',
         '', '## 本地原始页面 SHA-256', '', '| 文件 | SHA-256 |', '| --- | --- |']
for p in sorted((ROOT / 'raw').glob('*.html')):
    rows.append(f'| {p.name} | {hashlib.sha256(p.read_bytes()).hexdigest()} |')
(ROOT / 'SOURCES.md').write_text('\n'.join(rows)+'\n',encoding='utf-8')
report = (ROOT.parent.parent / 'docs/WALLPAPER-CREATOR-STUDY.md').read_text(encoding='utf-8')
ids = set(re.findall(r'sharedfiles/filedetails/\?id=(\d+)',report))
known = {d['id'] for d in data['details']}
assert ids <= known, ids-known
assert not data['errors'], data['errors']
print(f"Verified {len(ids)} report item IDs; {len(data['details'])} details; {data['unique_catalog_items']} catalog entries")
