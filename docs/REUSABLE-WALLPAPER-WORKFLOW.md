# 可复用壁纸工作流

这套壁纸把布局、数据桥接和主题资源分开。更换壁纸时只需要准备一张竖屏图片、一个预设 ID 和一组强调色，组件位置、时钟、额度、硬件和管理页逻辑继续复用。

## 换壁纸

1. 将图片放到 `public/assets/wallpapers/<id>.jpg`，源码留档可同步到 `design/assets/wallpapers/<id>.jpg`。
2. 在 `public/theme.js` 的 `PRESETS` 增加 `{ label, preset, accent, surface, background }`。`background` 只能使用包内 `assets/...` 路径。新增预设 ID 时同步 `public/theme.d.ts` 的类型与 `bridge/WallpaperBridge/Models.cs` 的预设校验；仅替换已有预设的图片和主题色无需改动组件。
3. 在管理页“壁纸主题与管理页”选择预设，必要时调整强调色和玻璃表面，点击保存。
4. 执行 `npm run check`。构建会把主题模块和全部壁纸复制到 `dist`。需要独立工坊包时，在 `scripts/prepare-workshop-variants.mjs` 中配置标题和预设，执行该脚本；当前输出为 `artifacts/Glasswp-Purple`、`artifacts/Glasswp-Angel`、`artifacts/Glasswp-Corgi`，详见 [工坊打包](GLASSWP-WORKSHOP.md)。

## 主题约定

- `original`：原紫色深色玻璃。
- `neon`：黄绿高对比图片，默认黄绿色强调色。
- `pool`：青蓝图片，默认浅色玻璃以保证黑色文字可读。
- 主题只改变颜色、表面和背景，不改变组件布局；因此同一套数据组件可以跨壁纸运行。
- 管理页主题单独保存为 `follow`、`light` 或 `dark`。`follow` 使用管理页所选壁纸预设的 `surface`；管理页模式不会改变壁纸的明暗设置。
- 通用 `dist` 壁纸跟随桥接中的主题设置。独立工坊包由 HTML 的 `data-wallpaper-preset` 固定预设，多套同时连接一个桥接时仍保留各自背景；管理页需要选择相应预设来预览同样的主题，不会自动探测 Wallpaper Engine 当前启用的项目。

## 验证和安装

开发验证：`npm run check`，再运行 `python tests/admin_theme_check.py`（本机需有 Playwright）。

正式候选：`scripts/build.ps1` 生成候选包，`scripts/verify.ps1` 检查前端、桥接、清单和回退，确认后由 `scripts/update-bridge.ps1` 安装到受保护运行目录。壁纸 Engine 包由 `scripts/package-wallpaper.mjs` 生成；不要直接把开发目录当作运行目录。

运行位置和配置仍以当前受保护目录及本机 DPAPI 配置为准，换主题不会复制或修改凭据。
