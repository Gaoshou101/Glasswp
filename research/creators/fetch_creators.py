"""Read public Steam Workshop catalogs and selected item descriptions."""
import concurrent.futures
import datetime
import json
from pathlib import Path
import re
import time
import requests
from bs4 import BeautifulSoup

ROOT = Path(__file__).resolve().parent
RAW = ROOT / 'raw'
RAW.mkdir(parents=True, exist_ok=True)
PROFILES = {'76561199211352973': 5, '76561198109148999': 7}
SELECTED = ['3492627662','3470764447','3463280673','3448877775','3448290956',
            '3414858021','3447534673','3509806978','3538758087','3494800319',
            '3599498791','3577513994','3570755196','3708363772','3709882938',
            '3666041758','3645009840','3617056047','3673609113','3773747576',
            '3573886911','2970648387']

def get(url, filename):
    path = RAW / filename
    if path.exists() and path.stat().st_size > 10000:
        return BeautifulSoup(path.read_bytes(), 'html.parser')
    for attempt in range(4):
        try:
            r = requests.get(url, timeout=35)
            r.raise_for_status()
            path.write_bytes(r.content)
            return BeautifulSoup(r.content, 'html.parser')
        except requests.RequestException:
            if attempt == 3:
                raise
            time.sleep(2 * (attempt + 1))

def catalog(arg):
    profile, page = arg
    url = f'https://steamcommunity.com/profiles/{profile}/myworkshopfiles/?appid=431960&numperpage=30&p={page}'
    s = get(url, f'catalog-{profile}-{page}.html')
    items = []
    for a in s.select('a[href*="sharedfiles/filedetails"]'):
        title = a.get_text(' ', strip=True)
        match = re.search(r'[?&]id=(\d+)', a.get('href',''))
        if title and match:
            items.append({'id': match[1], 'title': title, 'url': a['href'], 'profile': profile})
    full = s.get_text(' ', strip=True)
    return {'url': url, 'profile': profile, 'page': page, 'title': s.title.get_text(),
            'pagination': re.findall(r'Showing [\d,-]+ of [\d,]+ entries', full),
            'followers': re.findall(r'[\d,]+ Followers', full), 'items': items}

def detail(itemid):
    url = f'https://steamcommunity.com/sharedfiles/filedetails/?id={itemid}'
    s = get(url, f'item-{itemid}.html')
    def txt(sel):
        el = s.select_one(sel)
        return el.get_text('\n',strip=True) if el else ''
    description = txt('.workshopItemDescription')
    tags = [e.get_text(' ',strip=True) for e in s.select('.workshopTags')]
    authors = [{'name':txt_block.get_text(' ',strip=True).removesuffix(' Offline').removesuffix(' Online'),
                'url':block.select_one('a.friendBlockLinkOverlay')['href']}
               for block in s.select('.friendBlock')
               if (txt_block := block.select_one('.friendBlockContent')) and block.select_one('a.friendBlockLinkOverlay')]
    links = [{'text':a.get_text(' ',strip=True), 'url':a.get('href')}
             for a in s.select('.workshopItemDescription a[href]')]
    images = [{'src':a.get('src'),'data':a.get('data-fullsize')}
              for a in s.select('#highlight_strip img, #highlight_player_area img')]
    result = {'id':itemid, 'url':url,'title':txt('.workshopItemTitle'),'authors':authors,
              'tags':tags,'description':description,'links':links,'preview_images':images,
              'stats':txt('.detailsStatsContainerLeft'),
              'stats_values':txt('.detailsStatsContainerRight'),
              'public_stats':txt('.rightDetailsBlock'),'rating':txt('.numRatings'),
              'youtube_ids':re.findall(r'YOUTUBE_VIDEO_ID:\s*"([^"]+)"',str(s)),
              'usage_stats':txt('.stats_table')}
    (RAW / f'item-{itemid}.txt').write_text(s.get_text('\n',strip=True),encoding='utf-8')
    return result

def main():
    tasks = [(profile,page) for profile,n in PROFILES.items() for page in range(1,n+1)]
    errors = []
    def safe_detail(itemid):
        try:
            return detail(itemid)
        except requests.RequestException as exc:
            errors.append({'id': itemid, 'error': str(exc)})
            return None
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as ex:
        catalogs = list(ex.map(catalog,tasks))
        details = [r for r in ex.map(safe_detail,SELECTED) if r]
    unique = {r['id']:r for c in catalogs for r in c['items']}
    result = {'retrieved_at_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'catalogs':catalogs,'unique_catalog_items':len(unique),'items':list(unique.values()),
              'details':details,'errors':errors,'scope':'Public pages only; no Workshop content downloaded or executed'}
    (ROOT/'sources.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print('Catalog items:',len(unique),'Detailed works:',len(details),flush=True)
    for r in details:
        print('\nITEM',r['id'],r['title'],'TAGS',r['tags'],flush=True)
        print(r['description'][:6500],flush=True)
        print('LINKS',r['links'],flush=True)

if __name__ == '__main__':
    main()
