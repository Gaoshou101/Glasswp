import { cp, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const source = join(root, 'dist');
const output = join(root, 'artifacts', 'workshop');
await mkdir(join(output, 'assets'), { recursive: true });
for (const file of ['index.html', 'wallpaper.js', 'wallpaper.css', 'theme.js']) {
  await cp(join(source, file), join(output, file));
}
await cp(join(source, 'assets', 'wallpaper-original.jpg'), join(output, 'assets', 'wallpaper-original.jpg'));
await cp(join(source, 'assets', 'wallpapers'), join(output, 'assets', 'wallpapers'), { recursive: true });
await cp(join(source, 'assets', 'providers'), join(output, 'assets', 'providers'), { recursive: true });
const project = JSON.parse(await readFile(join(source, 'project.json'), 'utf8'));
if (project.general.properties.bridge_token.value !== '') throw new Error('The distribution ticket must be empty.');
project.title = '竖屏动态信息壁纸 · 磨砂玻璃仪表盘';
project.visibility = 'private';
project.description = `以 1600×2560 竖屏为基准的磨砂玻璃信息壁纸，支持常见竖屏分辨率。

功能
• 日期、时间与城市天气。
• CPAMP / Codex 多账号套餐余量、重置时间及倒计时。
• CPU、GPU、专用显存与物理内存监控，以及最近一分钟趋势。
• 三个模块独立调整显示、透明度、模糊、高光、大小与位置。

使用说明
这是 Web 壁纸。日期与时钟可独立运行；天气、额度和硬件数据需要另外运行 WallpaperBridge 本机桥接，本工坊包不包含桥接程序。
本次为仅自己可见的验证版本，使用本机现有桥接。在 http://127.0.0.1:17632/admin/ 复制展示票据，粘贴到 Wallpaper Engine 的“展示票据”属性。管理凭据只在本机桥接设置中填写。
展示票据、账号配置和缓存均不随工坊包上传。订阅后首次使用需要重新填写自己的展示票据。
部分 CPU 温度需要以管理员权限运行桥接；没有传感器证据时显示未知。
预览图中的账号和数据为示例，不代表真实余额或设备状态。

背景图的权利归原作者所有。本次仅用于个人私有验证。`;
await writeFile(join(output, 'project.json'), JSON.stringify(project, null, 2) + '\n');
await writeFile(join(root, 'artifacts', 'workshop-description.txt'), project.description + '\n');
await writeFile(join(output, '使用说明.txt'), project.description + '\n');

const allowed = ['assets', 'index.html', 'project.json', 'wallpaper.css', 'wallpaper.js', 'theme.js', '使用说明.txt', 'preview.png'];
const unexpected = (await readdir(output)).filter(file => !allowed.includes(file));
if (unexpected.length) throw new Error('Unexpected Workshop files: ' + unexpected.join(', '));
console.log('Private Workshop package prepared: ' + output);
