# 开发、验证与发布

## 环境

- Windows；桥接目标为 `net10.0-windows`，自包含发布目标为 `win-x64`。
- Node.js 20.19+ 或 22.12+。
- .NET 10 SDK。
- 浏览器回归额外需要 Python、Playwright 与本机 Chrome。

`release-common.ps1` 依次检查 `%LOCALAPPDATA%\WallpaperDashboard\devtools\dotnet`、Codex MSIX 对应的本地缓存目录，以及 PATH 中带 SDK 的 dotnet。发布桥接运行时不需要 SDK。

## 常用入口

在仓库根目录执行：

```powershell
npm ci
npm run dev
```

前端开发服务器只监听本机。实时数据仍需要运行桥接并配置展示票据。

```powershell
# 前端测试、TypeScript 与 Vite 构建
npm run check

# 完整候选：安装依赖、检查前端与桥接、自包含发布
.\scripts\build.ps1

# 前端、桥接和发布事务验证，不安装
.\scripts\verify.ps1

# 附加匿名浏览器回归
.\scripts\verify.ps1 -Browser -Python 'D:\Tools\Python\python.exe'

# 已构建候选后，在隔离端口演练桥接启动失败与恢复
.\scripts\verify.ps1 -ReleaseRuntime
```

浏览器测试通过匿名数据拦截验证布局和异常状态。截图与结果存放在 `artifacts/validation`，不进入 Git。测试说明以各脚本为准，避免将测试数量作为长期承诺。

README 展示图可在构建后运行 `python scripts/capture-readme.py` 重建，输出到 `docs/images`。脚本使用隔离页面和匿名示例，不读取实际桥接账号。安装入口与 ZIP 的验证范围见 [安装流程验收记录](INSTALLER-VALIDATION-2026-11-12.md)。

## 候选与安装

`build.ps1` 生成前端 `dist` 和桥接 `artifacts/staging`，不停止或覆盖运行桥接。`build-info.json` 记录构建 ID、源码提交、源码是否有未提交变更，以及文件 SHA256。

完整构建还会在 `artifacts/packages` 生成 `Glasswp-Bridge-<buildId>-win-x64.zip` 及 `.zip.sha256`。ZIP 内有同名顶层文件夹，包含双击安装入口、安装脚本与完整 `payload`。已有候选也可单独打包：

```powershell
.\scripts\package-bridge.ps1
```

候选验证通过后：

```powershell
.\scripts\install-bridge.ps1 -Check
.\scripts\install-bridge.ps1
```

统一安装入口会安装、启动、注册登录自启动并打开设置；高级选项见 [安装与连接](INSTALL.md)。已有部署也可继续使用原更新入口：

```powershell
# 保留原运行方式；若原来停止则只安装
.\scripts\update-bridge.ps1

# 原来停止时也在更新后启动
.\scripts\update-bridge.ps1 -Start
```

安装会通过 UAC 复制到受保护运行目录，在受保护副本上重新校验哈希与权限。已有桥接更新检查健康、构建 ID、进程路径、配置保留及硬件首个样本；失败时尝试恢复原目录和运行状态。

运行根目录下 `backups` 保留最近两份回退副本，`state` 保存安装/更新记录。传统桥接更新记录为 `state/bridge-update.json`，并复制到仓库 `artifacts/validation/bridge-update.json`。壁纸安装记录为 `artifacts/validation/wallpaper-update.json`；软件验收与物理桌面输入验收应分别记录。

## Wallpaper Engine 打包

`npm run build` 生成 `dist/project.json`。前端打包为经典 IIFE，避免本地文件 ES module 跨域问题；背景与样式随包分发。`dist` 中的展示票据为空。

```powershell
node scripts/prepare-workshop-variants.mjs
python tests/workshop_package_check.py
python tests/workshop_browser_check.py
python scripts/zip-workshop-variants.py
```

上述流程生成 Purple、Angel、Corgi 三个固定主题包及 ZIP，不上传创意工坊。已有发布 ID 的项目不会被打包脚本覆盖。发布步骤和图片权利说明见 [三套工坊包](GLASSWP-WORKSHOP.md)。

## 工程布局

| 路径 | 内容 |
|---|---|
| `src` | 时钟、天气、额度、硬件及交互组件 |
| `public/admin` | 本机设置页 |
| `public/theme.js` | 主题预设与共享颜色逻辑 |
| `bridge/WallpaperBridge` | Windows 桥接与数据来源 |
| `bridge/WallpaperBridge.Tests` | 桥接检查 |
| `wallpaper/project.json` | Wallpaper Engine 属性契约 |
| `scripts` | 构建、安装、打包与资源观察 |
| `tests` | 前端、浏览器和发布流程检查 |

新增主题时参考 [可复用壁纸工作流](REUSABLE-WALLPAPER-WORKFLOW.md)。变更展示字段或权限时同时核对 [数据契约](DATA-CONTRACT.md)。所有 PowerShell 脚本使用 UTF-8 BOM，保持 Windows PowerShell 5.1 兼容。

## 资源与稳定性观察

```powershell
.\scripts\measure-resources.ps1 -Samples 120 -IntervalSeconds 5 -Label after-change
.\scripts\observe-stability.ps1
python scripts/analyze-stability.py artifacts/analysis/<观察目录>/samples.jsonl
python scripts/ab-frontend-resources.py --rounds 3 --measure-minutes 6

# 默认只列出可清理项；显式 -Apply 才执行清理
.\scripts\prune-artifacts.ps1
```

常规资源采样是只读观察，不能直接作为受控 A/B 收益。稳定性分析按进程实例分段并剔除预热；前端 A/B 在隔离无头 Chrome 中回放匿名数据，不改变正式壁纸。

## 配置上下文

Windows DPAPI CurrentUser 将凭据绑定到 Windows 用户。Codex MSIX 上下文可能将 AppData 虚拟化至 `Packages/.../LocalCache/Local`；外部终端使用常规用户目录。持续使用同一入口，或明确传入原私有目录，避免连接错配置。

只分享未配对的发布内容；不要提交私有配置、已配对壁纸目录或含账号信息的日志与截图。第三方协议实现与图标来源见 [第三方说明](THIRD-PARTY-NOTICES.md) 和 [平台图标](PROVIDER-ICONS.md)。
