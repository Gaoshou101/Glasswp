import { cp, readFile, writeFile, mkdir } from 'node:fs/promises';
await mkdir('dist/assets', { recursive: true });
await cp('design/assets/wallpaper-original.jpg', 'dist/assets/wallpaper-original.jpg');
await cp('design/frosted-glass-v3-1600x2560.png', 'dist/preview.png');
const html = await readFile('index.html', 'utf8');
await writeFile('dist/index.html', html.replace('/design/assets/wallpaper-original.jpg', 'assets/wallpaper-original.jpg').replace('<script type="module" src="/src/main.ts"></script>', '<link rel="stylesheet" href="wallpaper.css"><script defer src="wallpaper.js"></script>'));
await cp('wallpaper/project.json', 'dist/project.json');
console.log('Wallpaper Engine package ready: dist/project.json');
