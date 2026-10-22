import { defineConfig } from 'vite';
import { readFileSync } from 'node:fs';
const buildInfo = (() => { try { return JSON.parse(readFileSync('artifacts/current-build.json', 'utf8')); } catch { return { buildId: 'development' }; } })();
export default defineConfig({
  define: { __BUILD_ID__: JSON.stringify(buildInfo.buildId) },
  base: './',
  build: {
    target: 'chrome100',
    lib: { entry: 'src/main.ts', name: 'PortraitWallpaper', formats: ['iife'], fileName: () => 'wallpaper.js', cssFileName: 'wallpaper' },
    rollupOptions: { output: { inlineDynamicImports: true } }
  }
});
