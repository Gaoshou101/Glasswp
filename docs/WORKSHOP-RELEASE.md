# 创意工坊首次私有发布

日期：2026-10-11，Asia/Shanghai。

## 上传项目

- 标题：竖屏动态信息壁纸 · 磨砂玻璃仪表盘。
- 可见性：私人，仅账号本人可见。
- 样式：Anime（动漫）。
- 内容分级：大众级（G），无血腥暴力、无性内容。
- 类型：Web。
- 上传源：`F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\myprojects\portrait-dashboard-workshop`。
- 现有本机项目：`portrait-dashboard`，保存本机展示票据，继续用于个人运行。

上传副本包含 7 个文件：入口 HTML、生产 JS/CSS、背景图、空票据的项目 JSON、匿名预览图、使用说明。没有桥接程序、管理凭据、私有配置、账号缓存或运行日志。

预览图从实际生产包渲染，账号、天气和硬件数值都是明确的示例。预览图约 371 KB。生产包浏览器检查无 JS 错误。

## 重建上传包

```powershell
npm run build
node scripts/prepare-workshop.mjs
# 使用 Python 3.12 及项目 .tools/python 中的 Playwright：
python scripts/render-workshop-preview.py
```

产物为 `artifacts/workshop/`，说明文本为 `artifacts/workshop-description.txt`。本次使用 Codex 依赖包中的 Python 3.12；系统 Python 3.11 与项目 greenlet 扩展不兼容。

`prepare-workshop.mjs` 只生成发布副本，不读取桥接配置或填入展示票据。后续更新应保留已发布项目的工坊 ID，并在 Wallpaper Engine 中更新该项目，避免创建重复作品。

## 首次使用

1. 订阅并下载工坊作品。
2. 运行本机已有 WallpaperBridge。
3. 在 `http://127.0.0.1:17632/admin/` 复制展示票据。
4. 在 Wallpaper Engine 该作品的“展示票据”属性中粘贴。管理凭据仅在本机桥接设置页填写。

日期与时钟无需桥接；天气、额度和硬件数据需要桥接。本次私有工坊验证使用现有桥接，工坊包不分发桥接程序。桥接没有自动随壁纸启动。

## 当前发布状态

已成功上传并订阅下载，可见性为私人。用户亲自完成 Steam 登录及更新后的创意工坊协议确认，Wallpaper Engine 编辑器显示上传成功。

- 工坊 ID：`3817627732`。
- 作品页：<https://steamcommunity.com/sharedfiles/filedetails/?id=3817627732>。
- 实际订阅目录：`F:\SteamLibrary\steamapps\workshop\content\431960\3817627732`。
- 发布源项目保留 `workshopid: "3817627732"`，后续使用该项目更新现有作品。

### 本地版本更新（2026-10-15）

当前竖屏实际使用本地 `portrait-dashboard-workshop`，用户确认后已更新为轻量液态版本，并同步 `portrait-dashboard`。旧文件及 WE 配置已备份，原非材质属性和其他显示器壁纸保留。正式源项目展示票据为空，工坊 ID 保留；1600×2560、30 FPS 下重载验证通过。此更新仅应用于本机，Steam 已上传版本及订阅下载目录尚未同步。详见 [正式本地采用记录](LIQUID-GLASS-PREVIEW.md#正式本地桌面采用2026-10-15)。

## 订阅运行验证

2026-10-11 已完成以下检查：

- 生产构建及 9 项前端测试通过。
- 下载的 HTML、JS、CSS、背景图、匿名预览及使用说明与发布源的 SHA256 一致。
- 工坊下载包与发布源的展示票据均为空。本机票据只填写到 Wallpaper Engine 的订阅版属性中。
- 从真实订阅目录加载的浏览器验证通过：空票据提示连接，应用本机票据后天气、3 个账号额度、硬件数值正常，JavaScript 错误为 0。
- Wallpaper Engine 的竖屏配置明确指向上述工坊下载目录的 `index.html`。桥接收到实际宿主上报：`wallpaper-engine`、1600×2560、30 FPS、未暂停；持续读取本机数据。
- 天气、额度和硬件状态均为 `ok`，CPU 与 GPU 温度可用。

验证期间 Wallpaper Engine 的 Web 渲染进程曾出现启动异常；保留配置并重启后恢复，订阅版的本机票据已通过属性界面配置完成。

浏览器检查结果保存在 `artifacts/validation/workshop-download-result.json`；实际宿主上报和下载文件哈希保存在 `artifacts/validation/workshop-host-result.json`。这些记录仅包含验证结果，不包含票据、账号标识或管理凭据。
