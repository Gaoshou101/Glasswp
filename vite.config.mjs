import { defineConfig } from 'vite';
export default defineConfig({
  base: './',
  build: {
    target: 'chrome100',
    lib: { entry: 'src/main.ts', name: 'PortraitWallpaper', formats: ['iife'], fileName: () => 'wallpaper.js', cssFileName: 'wallpaper' },
    rollupOptions: { output: { inlineDynamicImports: true } }
  }
});
