# 安装与连接

Glasswp 由 Wallpaper Engine 壁纸和 Windows 本机桥接组成。日期与时钟可独立运行；天气、AI 余量和硬件监控需要桥接。发布桥接为 Windows x64 自包含程序，运行时无需安装 Node.js 或 .NET。

## 安装桥接

### 从自包含包安装

从 [GitHub Releases](https://github.com/Gaoshou101/Glasswp/releases/latest) 下载 `Glasswp-Bridge-v1.0.0-win-x64.zip`，解压并进入同名文件夹，双击 `install-bridge.cmd`。普通本地构建使用 `Glasswp-Bridge-<buildId>-win-x64.zip` 命名，安装方式相同。也可在该目录的 PowerShell 中执行：

```powershell
.\install-bridge.cmd
```

安装器先校验发布文件，随后请求一次 UAC，将桥接安装到受保护运行目录，启动桥接、注册登录自启动，并打开本机设置页。使用原 Windows 用户接受 UAC，确保已有 DPAPI 凭据仍可解密。

正式 ZIP 由 GitHub Actions 从发布标签构建。旁边的 `.zip.sha256` 可用于核对压缩包 SHA256，`*-build-info.json` 记录源码提交和文件哈希。包内 `payload/wallpaper` 是展示票据为空的通用壁纸，Wallpaper Engine 需另行安装。

### 从源码安装

准备 Node.js 20.19+ 或 22.12+、.NET 10 SDK，在仓库根目录执行：

```powershell
.\scripts\build.ps1
.\scripts\install-bridge.ps1
```

构建包括依赖安装、前端检查、桥接检查和自包含发布，只生成候选文件。第二条命令负责实际安装。无需以管理员身份构建。

### 安装选项

```powershell
# 只检查安装包和安装计划，不安装
.\scripts\install-bridge.ps1 -Check

# 指定已构建的发布目录
.\scripts\install-bridge.ps1 -Source 'D:\Downloads\Glasswp-Bridge\payload'

# 使用其他盘：运行根目录必须直接位于盘符根下
.\scripts\install-bridge.ps1 -RuntimeRoot 'D:\WallpaperDashboard'

# 不新建或修改登录自启动任务
.\scripts\install-bridge.ps1 -NoAutostart

# 保留已使用的私有配置目录，并且不自动打开浏览器
.\scripts\install-bridge.ps1 -DataDirectory 'D:\GlasswpData\private' -NoOpenSettings
```

默认运行根目录为 `E:\WallpaperDashboard`。没有 E 盘时显式指定可用盘上的 `-RuntimeRoot`；此选项用于运行程序，不会迁移源码或私有配置。`-NoAutostart` 保留已有任务，需要取消已有自启动时使用下文的 `-Remove`。

上述示例使用仓库脚本；自包含包用户将命令开头替换为 `.\install-bridge.cmd`，选项相同，且入口会自动选择包内 `payload`。

升级时重复使用安装入口。已有桥接更新会校验构建、进程路径、配置与硬件首个样本；失败时尝试恢复原发布和运行状态。安装日志与报告见运行根目录下的 `state`。

## 首次配置

打开 [本机设置](http://127.0.0.1:17632/admin/)，按需要配置：

1. **天气与时间**：选择城市、天气来源、显示时区和世界时区。Open-Meteo 与 MET Norway 无需 API Key；其他来源需填写各自凭据。高德需 6 位行政区编码；和风需控制台提供的 API Host。
2. **AI 套餐来源**：选择 CPAMP、本机自动读取、Magpie、CPA 或 Sub2API。只读取当前所选来源，连接与账号设置分别保存。
3. **账号与刷新**：选择显示账号、设置别名和顺序。确认来源支持后启用自动刷新；Magpie 读取其缓存，更新上游缓存仍由 Magpie 完成。
4. **硬件传感器**：核对 CPU/GPU、显存和内存读数。部分温度需要管理员权限，具体支持依设备和驱动而异。

密钥只在本机设置页填写。没有额度证据的账号显示未知；倒计时归零不会自行恢复成满额。

## 连接 Wallpaper Engine

### 使用工坊壁纸包

1. 在 Wallpaper Engine 中打开壁纸包的 `project.json`。
2. 在本机管理页复制“展示票据”。
3. 粘贴到 Wallpaper Engine 右侧该壁纸的“展示票据”属性。本机数据地址保持 `http://127.0.0.1:17632`。
4. 在 Wallpaper Engine 属性中调整组件显示、位置、大小和玻璃材质。

工坊包不包含桥接或管理凭据。`Glasswp-Purple`、`Glasswp-Angel`、`Glasswp-Corgi` 固定各自背景，连接同一个桥接也不会互相切换主题。

### 从源码安装本地壁纸

构建后，保持桥接运行，在仓库根目录执行以下命令，并将路径改为实际 Wallpaper Engine 安装目录：

```powershell
.\scripts\install-wallpaper.ps1 -WallpaperEnginePath 'D:\SteamLibrary\steamapps\common\wallpaper_engine' -PreviewWindow
```

脚本将 `dist` 安装为本地项目，自动配对展示票据、保留已有属性并备份旧目录。它默认不更换桌面壁纸；可在 Wallpaper Engine 中选择新项目应用。

需要命令行应用时，先确认目标显示器索引，再增加 `-Apply -Monitor <索引>`；也可使用已核实的 `-Location`。两种显示器选择方式只能使用一种。

## 日常维护

以下命令在仓库或自包含安装包目录执行。使用其他运行目录时，为相应脚本追加相同的 `-RuntimeRoot`。

```powershell
# 启动并打开设置；读取部分温度需要 -Elevated
.\scripts\start-bridge.ps1 -OpenSettings
.\scripts\start-bridge.ps1 -Elevated -OpenSettings

# 停止（管理员桥接需要管理员 PowerShell）
.\scripts\stop-bridge.ps1

# 取消登录自启动；不会停止已经运行的桥接
.\scripts\install-bridge-autostart.ps1 -Remove
```

登录自启动使用 `WallpaperDashboardBridge` 计划任务，在当前用户登录约 15 秒后启动；不需要保存 Windows 密码。异常退出后任务可按分钟重试，最多三次。

## 排错

| 现象 | 检查方式 |
|---|---|
| 找不到候选发布 | 源码用户先运行 `build.ps1`；包用户完整解压，保留目录结构，再运行 `-Check`。 |
| E 盘不存在 | 用 `-RuntimeRoot 'D:\WallpaperDashboard'` 指定可用盘；运行根目录需直接位于盘符根下。 |
| 端口被占用 | 检查 `http://127.0.0.1:17632/v1/health`；安装器不会接管其他服务的端口。 |
| 更新提示旧运行位置 | 检查旧任务和进程路径。仓库旧运行方式需按 [运行目录加固](RUNTIME-HARDENING-2026-11-07.md) 迁移，勿并行启动两份桥接。 |
| 温度为空 | 确认桥接具有管理员权限，再核对管理页传感器列表；硬件不提供的读数不会被估算。 |
| 壁纸显示票据无效 | 从当前桥接重新复制展示票据，填写到实际使用的壁纸项目属性中。 |
| 修改 Studio 后桌面没变化 | Studio 设置仅保存在当前浏览器；桌面外观需要在 Wallpaper Engine 属性中调整。 |
| 更换入口后账号配置消失 | 核对 Windows 用户与私有目录；MSIX 容器和普通终端的 AppData 上下文可能不同，使用 `-DataDirectory` 明确原目录。 |

## 运行目录与凭据

运行根目录中的 `current` 存放正在使用的发布，`backups` 保留最近两份桥接回退副本，`state` 存放记录。目录只允许 Administrators 和 SYSTEM 写入，日常源码修改不会覆盖正在运行的程序。

私有配置默认位于 `%LOCALAPPDATA%\WallpaperDashboard\private`，使用 Windows DPAPI CurrentUser 加密，目录权限限制为当前用户与 SYSTEM。不要把私有目录或已配对的 Wallpaper Engine 项目当作分享包。

展示票据只用于规范化展示数据、宿主状态和固定用量刷新，不等同于管理凭据。分享壁纸应使用票据为空的 `dist` 或独立工坊包。访问边界见 [数据契约](DATA-CONTRACT.md)。
