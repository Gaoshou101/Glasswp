import { cp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const source = join(root, 'dist');
const outRoot = join(root, 'artifacts');
const variants = [
  { id: 'purple', title: 'Glasswp-Purple', preset: 'original', accent: '#dcc6ff', surface: 'dark', sourceImage: 'assets/wallpaper-original.jpg', preview: 'preview.png' },
  { id: 'angel', title: 'Glasswp-Angel', preset: 'neon', accent: '#e5f34d', surface: 'dark', sourceImage: 'assets/wallpapers/neon.jpg', preview: 'assets/wallpapers/neon.jpg' },
  { id: 'corgi', title: 'Glasswp-Corgi', preset: 'pool', accent: '#147f91', surface: 'light', sourceImage: 'assets/wallpapers/pool.jpg', preview: 'assets/wallpapers/pool.jpg' },
];

for (const variant of variants) {
  const output = join(outRoot, `workshop-${variant.id}`);
  await rm(output, { recursive: true, force: true });
  await mkdir(join(output, 'assets'), { recursive: true });
  for (const file of ['wallpaper.js', 'wallpaper.css', 'theme.js']) await cp(join(source, file), join(output, file));
  await cp(join(source, 'assets'), join(output, 'assets'), { recursive: true });
  await cp(join(source, variant.sourceImage), join(output, 'assets', 'wallpaper-original.jpg'));
  await cp(join(source, variant.preview), join(output, 'preview.png'));

  const boot = `<script>window.__GLASSWP_PIN_THEME=${JSON.stringify(variant.preset)};try{const k='wallpaper.dashboard.v1',s=JSON.parse(localStorage.getItem(k)||'{}');s.theme={preset:${JSON.stringify(variant.preset)},accent:${JSON.stringify(variant.accent)},surface:${JSON.stringify(variant.surface)},background:'assets/wallpaper-original.jpg'};localStorage.setItem(k,JSON.stringify(s))}catch{}</script>`;
  let html = await readFile(join(source, 'index.html'), 'utf8');
  html = html.replace('<link rel="stylesheet" href="wallpaper.css"><script defer src="wallpaper.js"></script>', `<link rel="stylesheet" href="wallpaper.css">${boot}<script defer src="wallpaper.js"></script>`);
  await writeFile(join(output, 'index.html'), html);

  let js = await readFile(join(output, 'wallpaper.js'), 'utf8');
  js = js.replace('if(e.appearance){', 'if(e.appearance&&!window.__GLASSWP_PIN_THEME){');
  await writeFile(join(output, 'wallpaper.js'), js);
  const project = JSON.parse(await readFile(join(source, 'project.json'), 'utf8'));
  project.title = variant.title;
  project.visibility = 'private';
  project.description = `Glasswp reusable vertical dashboard wallpaper. Theme: ${variant.title}. Web wallpaper with local bridge support for clock, weather, AI quota and hardware data. Background artwork is supplied by the project owner; keep attribution with the source artwork when redistributing.`;
  delete project.workshopid;
  await writeFile(join(output, 'project.json'), JSON.stringify(project, null, 2) + '\n');
  await writeFile(join(output, '使用说明.txt'), `${variant.title}\n\n${project.description}\n`);
}
console.log('Prepared Glasswp-Purple, Glasswp-Angel and Glasswp-Corgi workshop packages.');
