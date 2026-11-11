import { cp, mkdir, readFile, writeFile, readdir, lstat, realpath, rename } from 'node:fs/promises';
import { join, resolve, relative, sep } from 'node:path';
import { createHash, randomUUID } from 'node:crypto';

const root = await realpath(resolve(import.meta.dirname, '..'));
const source = join(root, 'dist');
const outRoot = join(root, 'artifacts');
const idKey = key => /workshop.*id|published.*file.*id/i.test(key);
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const variants = [
  { title: 'Glasswp-Purple', preset: 'original', background: 'assets/wallpaper-original.jpg', description: '紫色玻璃 · 白发角色', accent: '#dcc6ff', surface: 'dark' },
  { title: 'Glasswp-Angel', preset: 'neon', background: 'assets/wallpapers/neon.jpg', description: '黄绿玻璃 · 微笑天使', accent: '#e5f34d', surface: 'dark' },
  { title: 'Glasswp-Corgi', preset: 'pool', background: 'assets/wallpapers/pool.jpg', description: '青蓝玻璃 · 泳池柯基', accent: '#147f91', surface: 'light' },
];

async function exists(path) {
  try { await lstat(path); return true; } catch (error) { if (error.code === 'ENOENT') return false; throw error; }
}
async function assertPlainTree(path) {
  const info = await lstat(path);
  if (info.isSymbolicLink()) throw new Error(`Links are not allowed: ${path}`);
  if (info.isDirectory()) for (const name of await readdir(path)) await assertPlainTree(join(path, name));
}
function stripPublicationIds(value) {
  if (!value || typeof value !== 'object') return;
  for (const key of Object.keys(value)) {
    if (idKey(key)) delete value[key];
    else stripPublicationIds(value[key]);
  }
}
function hasPublicationId(value) {
  if (!value || typeof value !== 'object') return false;
  return Object.entries(value).some(([key, child]) => idKey(key) || hasPublicationId(child));
}
async function fileMap(path) {
  const files = {};
  async function visit(folder) {
    for (const name of (await readdir(folder)).sort()) {
      const item = join(folder, name), info = await lstat(item);
      if (info.isSymbolicLink()) throw new Error(`Links are not allowed: ${item}`);
      if (info.isDirectory()) await visit(item);
      else files[relative(path, item).split(sep).join('/')] = digest(await readFile(item));
    }
  }
  await visit(path);
  return files;
}
async function verifySource() {
  await assertPlainTree(source);
  const manifest = JSON.parse(await readFile(join(source, 'build-info.json'), 'utf8'));
  const actual = await fileMap(source);
  delete actual['build-info.json'];
  if (JSON.stringify(Object.keys(actual).sort()) !== JSON.stringify(Object.keys(manifest.files).sort())) throw new Error('Source build file inventory mismatch');
  for (const [name, hash] of Object.entries(manifest.files)) if (actual[name] !== hash) throw new Error(`Source build hash mismatch: ${name}`);
  const js = await readFile(join(source, 'wallpaper.js'), 'utf8');
  if (!js.includes('wallpaperPreset')) throw new Error('Build is missing the data-wallpaper-preset runtime contract; rebuild before packaging.');
  return manifest;
}

await mkdir(outRoot, { recursive: true });
if ((await lstat(outRoot)).isSymbolicLink()) throw new Error('Artifacts directory must not be a link');
if (await realpath(outRoot) !== resolve(root, 'artifacts')) throw new Error('Artifacts physical path is outside the repository');
const sourceManifest = await verifySource();
const results = [];

for (const variant of variants) {
  const output = join(outRoot, variant.title);
  if (await exists(output)) {
    await assertPlainTree(output);
    const oldProject = join(output, 'project.json');
    if (!await exists(oldProject)) throw new Error(`Refusing to replace an unrecognized directory: ${output}`);
    if (hasPublicationId(JSON.parse(await readFile(oldProject, 'utf8')))) throw new Error(`Refusing to replace a published Workshop project: ${output}`);
  }
  const temporary = join(outRoot, `.workshop-staging-${randomUUID()}`);
  await mkdir(temporary);
  for (const file of ['wallpaper.js', 'wallpaper.css', 'theme.js', 'favicon.svg']) await cp(join(source, file), join(temporary, file));
  await cp(join(source, 'assets'), join(temporary, 'assets'), { recursive: true });
  const previewBytes = await readFile(join(source, variant.background));
  if (previewBytes[0] !== 0xff || previewBytes[1] !== 0xd8 || previewBytes[2] !== 0xff) throw new Error(`Expected a real JPEG preview for ${variant.title}`);
  await writeFile(join(temporary, 'preview.jpg'), previewBytes);

  let html = await readFile(join(source, 'index.html'), 'utf8');
  if (!/<html\b/i.test(html)) throw new Error('HTML root element is missing');
  html = html.replace(/<html\b([^>]*)>/i, (_, attributes) => `<html${attributes.replace(/\sdata-wallpaper-preset=(?:"[^"]*"|'[^']*')/gi, '')} data-wallpaper-preset="${variant.preset}">`);
  html = html.replace(/<title>[^<]*<\/title>/i, `<title>${variant.title}</title>`);
  if (!/<img\b[^>]*class="art"[^>]*src="/i.test(html)) throw new Error('Wallpaper artwork element is missing');
  html = html.replace(/(<img\b[^>]*class="art"[^>]*src=")[^"]*(")/i, `$1${variant.background}$2`);
  await writeFile(join(temporary, 'index.html'), html);
  const project = JSON.parse(await readFile(join(source, 'project.json'), 'utf8'));
  stripPublicationIds(project);
  project.title = variant.title;
  project.file = 'index.html';
  project.preview = 'preview.jpg';
  project.visibility = 'private';
  if (!project.general?.properties?.bridge_token) throw new Error('Distribution bridge_token property is missing');
  project.general.properties.bridge_token.value = '';
  project.description = `${variant.title} · ${variant.description}\n\n可复用的竖屏玻璃信息壁纸，支持日期时间、天气、AI 套餐余量和硬件监控。日期与时钟可独立运行；其余数据需要单独安装并运行本机 WallpaperBridge。\n\n完整源码与桥接安装说明见 Glasswp GitHub 仓库。展示票据、账号配置和缓存不随工坊包分发。\n\n预览图仅展示背景原画，不包含真实账号、用量或设备信息。背景图由用户提供，图片权利归原作者所有；使用和转载时请保留原作者署名与授权信息。`;
  await writeFile(join(temporary, 'project.json'), JSON.stringify(project, null, 2) + '\n');
  await writeFile(join(temporary, '使用说明.txt'), `${variant.title}\n${variant.description}\n\n运行步骤\n1. 订阅或用 Wallpaper Engine 打开此包的 project.json。\n2. 日期与时钟无需额外配置。天气、AI 额度和硬件数据需要先安装并启动 Glasswp / WallpaperBridge 本机桥接。\n3. 打开 http://127.0.0.1:17632/admin/，在本机配置天气和 AI 数据来源。管理凭据只在本机管理页填写。\n4. 在管理页复制展示票据，粘贴到 Wallpaper Engine 中此壁纸的“展示票据”属性。\n5. 在 Wallpaper Engine 属性中调整组件显示、大小、位置、玻璃透明度和模糊。此包固定为 ${variant.title} 主题，连接桥接不会切换背景。\n\n手动发布\n1. 在 Wallpaper Engine 壁纸编辑器中打开本目录中的 project.json。\n2. 选择发布到创意工坊，标题使用 ${variant.title}，预览图选择 preview.jpg。\n3. 本包默认“私人”；根据自己的发布意图调整可见性。\n4. 新项目不包含旧工坊 ID。发布成功后保留 Wallpaper Engine 写入的工坊 ID，以便以后更新同一作品。\n5. 如果程序显示需要接受新的 Steam 或创意工坊协议，请本人阅读并确认。\n\n背景来源及权利\n背景图由用户提供，图片权利归原作者所有；使用和转载时请保留原作者署名与授权信息。\n\n安全说明\n本包不包含桥接程序、管理凭据、展示票据、账号缓存或运行日志。预览图是背景原画，不含真实账号信息。\n`);
  const files = await fileMap(temporary);
  if (hasPublicationId(project) || project.general.properties.bridge_token.value !== '') throw new Error('Unsafe publication metadata');
  if (files['wallpaper.js'] !== sourceManifest.files['wallpaper.js']) throw new Error('Runtime JS must be copied without modification');
  await writeFile(join(temporary, 'package-info.json'), JSON.stringify({ title: variant.title, preset: variant.preset, background: variant.background, accent: variant.accent, surface: variant.surface, sourceBuildId: sourceManifest.buildId, files }, null, 2) + '\n');
  let backup = null;
  if (await exists(output)) { backup = `${output}.previous-${randomUUID()}`; await rename(output, backup); }
  try { await rename(temporary, output); }
  catch (error) { if (backup) await rename(backup, output); throw error; }
  results.push({ title: variant.title, path: output, preset: variant.preset, background: variant.background, sourceBuildId: sourceManifest.buildId, files: Object.keys(files).length + 1, backup });
}
console.log(JSON.stringify({ packages: results }, null, 2));
