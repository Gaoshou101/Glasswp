import { execFileSync } from 'node:child_process';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { createHash, randomBytes } from 'node:crypto';
import { resolve, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = resolve(import.meta.dirname, '..');
export async function describeBuild() {
  let commit = null, dirty = true;
  try {
    commit = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    dirty = !!execFileSync('git', ['status', '--porcelain', '--untracked-files=normal', '--', 'src', 'bridge', 'scripts', 'tests', 'public', 'wallpaper', '.github', 'install-bridge.cmd', 'package.json', 'package-lock.json', 'vite.config.mjs', 'tsconfig.json', 'index.html', 'design/assets', 'design/frosted-glass-v3-1600x2560.png'], { cwd: root, encoding: 'utf8' }).trim();
  } catch { /* Source archives have no Git metadata. */ }
  const version = JSON.parse(await readFile(join(root, 'package.json'), 'utf8')).version;
  return { buildId: `${new Date().toISOString().replace(/\D/g, '').slice(0, 14)}-${randomBytes(4).toString('hex')}`, builtAt: new Date().toISOString(), commit, dirty, version, schemaVersion: 1 };
}
export async function manifest(directory, info) {
  const files = {};
  async function visit(folder) {
    for (const entry of (await readdir(folder, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
      const path = join(folder, entry.name);
      if (entry.isSymbolicLink()) throw new Error('Release packages must not contain links');
      if (entry.isDirectory()) await visit(path);
      else if (resolve(path) !== resolve(directory, 'build-info.json')) files[relative(directory, path).replaceAll('\\', '/')] = createHash('sha256').update(await readFile(path)).digest('hex');
    }
  }
  await visit(directory);
  await writeFile(join(directory, 'build-info.json'), JSON.stringify({ ...info, files }, null, 2) + '\n');
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv[2] === '--release') {
    const info = JSON.parse(await readFile(join(root, 'artifacts/current-build.json'), 'utf8'));
    await manifest(resolve(process.argv[3]), info);
  } else {
    await mkdir(join(root, 'artifacts'), { recursive: true });
    await writeFile(join(root, 'artifacts/current-build.json'), JSON.stringify(await describeBuild(), null, 2) + '\n');
  }
}
