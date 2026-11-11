import { cp, readFile, writeFile, mkdir } from 'node:fs/promises';
import { manifest } from './build-info.mjs';
await mkdir('dist/assets', { recursive: true });
// Vite copies public/ as the runtime source of truth. Design references must
// not overwrite a wallpaper or palette that was updated only in public/.
await cp('design/frosted-glass-v3-1600x2560.png', 'dist/preview.png');
const html = await readFile('index.html', 'utf8');
await writeFile('dist/index.html', html.replace('/design/assets/wallpaper-original.jpg', 'assets/wallpaper-original.jpg').replace('<script type="module" src="/src/main.ts"></script>', '<link rel="stylesheet" href="wallpaper.css"><script defer src="wallpaper.js"></script>'));
await cp('wallpaper/project.json', 'dist/project.json');
await manifest('dist', JSON.parse(await readFile('artifacts/current-build.json', 'utf8')));
console.log('Wallpaper Engine package ready: dist/project.json');
