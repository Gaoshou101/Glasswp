# 竖屏动态信息壁纸

Windows / Wallpaper Engine Web 壁纸，沿用已确认的 [v3 玻璃设计](design/GLASS-DESIGN.md)。以 1600×2560 为基准自适应竖屏，保留角色面部留白。

下一阶段以稳定性与低占用为优先，实施顺序、代码依据和验收条件见 [优化路线](docs/OPTIMIZATION-ROADMAP.md)。

新增可预览的轻量液态风格：鼠标光泽、按压回弹、额度翻页过渡、刷新状态动画。点击卡片展开液态玻璃悬浮视图：账号上方为请求数、成功率、Token、CPAMP 预估费用与最后活动，时间范围与 CPAMP 一致，默认 7 天，可切换今天、昨天、14 天、30 天、全部或自定义；下方请求详情每页 20 条，支持较早/较新请求翻页与首字/耗时，旁边显示 Codex 可用重置次数；硬件提供带刻度的曲线、真实样本统计和采样时点；天气提供未来 12 个小时的温度、降水概率、预计降水量和切换城市入口。展开与收回使用短暂弹性过渡，面部留白继续保留。液态模式闲置时停止视觉调度，可切回原版磨砂；设置与验证见 [液态玻璃预览说明](docs/LIQUID-GLASS-PREVIEW.md)。

2026-10-15 已按用户确认将此版本应用到正式本地桌面，并同步本地开发副本；材质为液态 83/26/68，原有显示设置保留，重载验证通过。更新前已备份；Steam 创意工坊远端仍为此前发布版本。

- 上：时钟、日期、可搜索选择城市的天气（默认苏州）。
- 中：CPAMP 多账号额度卡片（每页最多三个账号），可切换单账号轮播；显示周期、重置时间与倒计时。
- 下：CPU、物理 GPU 的利用率与温度，专用显存与物理内存的使用率/容量，以及最近一分钟趋势。
- 三个组件独立启用、材质、大小与位置。透明度 0–100%、模糊 0–64、高光 0–100%，文字保持清晰。

壁纸正常运行时隐藏连接成功、来源、快照时间和规则说明，仅在断连、数据过期或缺少额度时保留简短提示。额度分页位于标题右侧，天气只显示城市；模块高度与间距更紧凑，保留角色面部留白。

“套餐余量”标题右侧的「刷新」向上游获取最新用量；刷新期间禁止重复点击，多个窗口共用桥接任务，完成后读取新快照。管理页「卡片显示顺序」支持上移/下移关注账号并保存，顺序同时用于多账号卡片与单账号轮播，重新打开后保留。

## 本机运行

本次已生成 `artifacts/bridge/WallpaperBridge.exe` 自包含程序，以及 `dist/project.json` 壁纸包。运行发布程序无需另装 .NET。

```powershell
# 普通权限；部分温度可能不可用
./scripts/start-bridge.ps1 -OpenSettings

# 本机已验证 CPU Package 温度需要管理员权限，会弹出 UAC
./scripts/start-bridge.ps1 -Elevated -OpenSettings

# 安装到现有 Wallpaper Engine，打开独立预览窗口
./scripts/install-wallpaper.ps1 -PreviewWindow
```

[本机数据设置](http://127.0.0.1:17632/admin/)：[壁纸预览与外观设置](http://127.0.0.1:17632/?studio)。先保存 CPAMP 地址和管理凭据，再读取账号列表、选择关注账号并保存别名。天气城市支持搜索中文、拼音或英文名；搜索结果会显示行政区，避免苏州与宿州混淆。

本机设置的「用量自动刷新」默认开启，每 **10 分钟**向上游获取关注账号的新用量。间隔可设为 **1–10080 分钟**，支持关闭自动刷新或点击「立即刷新用量」。页面显示上次尝试、最近成功、下次时间及逐账号结果；修改间隔后按最近一次刷新完成时间重新计算。

默认 Wallpaper Engine 安装路径为本机已检测的位置 `F:\SteamLibrary\steamapps\common\wallpaper_engine`。安装脚本校验构建清单，自动填入展示票据，按名称迁移旧属性并保留项目身份；完整旧目录备份在目标盘的 `projects/dashboard-backups`。备份可能含本机展示票据，仅供本地回退。

若需要应用到桌面，先在 WE 确认显示器索引（从 0 开始），再运行：

```powershell
./scripts/install-wallpaper.ps1 -Apply -Monitor <已确认的索引>

# 更新当前使用的另一个本地项目；同一路径会先关闭再打开
./scripts/install-wallpaper.ps1 -Destination 'F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\myprojects\portrait-dashboard-workshop' -Apply -Location '<已确认的显示器 Location>'
```

WE 属性中提供三个组件的材质、显示、大小、水平与垂直位置、时区、12/24 小时、秒、柔光动态、趋势、额度显示方式、翻页间隔及底部留白。可用底部留白避让任务栏。桌面外观在 Wallpaper Engine 中选中正在使用的壁纸后，通过右侧属性调整并由宿主保存。Studio 仅用于浏览器预览，设置只保存在当前浏览器，不会同步到 Wallpaper Engine；WE 的属性也不会自动回填 Studio。天气、账号和传感器等共享数据仍在本机数据设置页管理。多账号卡片默认启用，超过三个账号或两个额度窗口时分页，所有账号与窗口均可访问。

WE 属性或浏览器「组件与连接」中可勾选「忽略 Codex 基础模型周窗口（luna-release）」，默认未勾选。开启后该窗口不参与展示、分页或数据过期提示；卡片和单账号轮播均生效。主套餐周窗口、其他模型与平台保持显示，取消勾选即可恢复。

同一区域另有「忽略 Codex 绘图额度（imagegen）」，默认未勾选，独立控制绘图额度的展示、分页和过期提示。普通日额度保持显示；如果某账号的全部窗口均被隐藏，保留账号并提示相应额度已忽略。取消勾选恢复显示，不修改上游或本地额度记录。

停止桥接：`./scripts/stop-bridge.ps1`。管理员桥接需要从管理员 PowerShell 停止。

如需重启后自动恢复数据，运行一次 `./scripts/install-bridge-autostart.ps1` 并接受 UAC。脚本注册 `WallpaperDashboardBridge` 计划任务，在当前用户登录 Windows 15 秒后以管理员权限隐藏启动，保留 CPU 温度读取；固定原有 DPAPI 配置目录，无需保存 Windows 密码。任务允许电池供电、没有运行时限，进程异常退出后每分钟重试，最多三次。可用 `-DataDirectory <原 private 目录>` 指定已有配置。

取消自动启动：`./scripts/install-bridge-autostart.ps1 -Remove`。取消任务后可继续使用手动启动脚本；正在运行的桥接仍可用停止脚本关闭。

## 已验证与限制

详见 [实施验证记录](docs/IMPLEMENTATION-VALIDATION.md)。本机已读到 CPU/GPU 利用率、CPU Package（管理员模式）、GPU Core 温度、约 16 GiB 专用显存和约 48 GiB 物理内存。按用户要求，已移除内存温度采集、选择和展示。

CPAMP 的 `database disk image is malformed (11)` 已在 2026-10-10 修复。先保留一致性副本，在副本上重建并逐表核对，再对停服后的最新数据库重建切换；78,292 条有效历史请求、47 张业务表的有效记录完整保留，三个关注账号均成功返回额度快照。原库、WAL、配置和密钥保存在远端私有回退目录。见 [修复记录](docs/CPAMP-REPAIR.md)。壁纸仍逐账号隔离查询，以成功读取的 CPAMP 快照为准；响应头缓存仅补充缺失窗口，或在快照读取失败时回退到较新的已有证据，避免覆盖 CPAMP 选定的余量和重置时间。没有证据的窗口保持未知。

桥接运行期间统一调度上游刷新，多个壁纸或预览窗口共用一次刷新。当前支持 Codex：通过 CPAMP/CPA 获取当前额度，并将观察写回 CPAMP；其他平台继续展示已有快照，设置页提示暂不支持自动刷新。失败账号保留上次数据，等待设定的下一次刷新；重启保留刷新时间。配额快照仍每 60 秒读取，前端轮询只读取本机数据。

额度数据超过 15 分钟标记过期（选择更长的上游刷新间隔时仍适用）；倒计时归零显示“等待更新”，不擅自恢复余额。断网保留上次数据和时间。天气为 Open-Meteo 模型数据，非实地传感器观测；每 15 分钟刷新当前条件与小时预报，超过 30 分钟或连接异常显示上次天气。缺失预报显示未知，真实零降水保留为 0% / 0.0 mm。

## 创意工坊私有验证

首次上传使用独立的 `portrait-dashboard-workshop` 项目，设为仅自己可见。工坊包展示票据为空，预览图使用示例数据；订阅后在 WE 属性中填入本机展示票据。天气、额度和硬件功能需要本机桥接，工坊包不含桥接程序。发布准备、重建方法及当前状态见 [创意工坊发布记录](docs/WORKSHOP-RELEASE.md)。

WE 中的「竖屏动态信息壁纸」是本地开发版（`portrait-dashboard`），「竖屏动态信息壁纸 · 磨砂玻璃仪表盘」是工坊发布版本（`portrait-dashboard-workshop` / 工坊 ID `3817627732`）。两者沿用相同画面，但宿主属性分别保存；调整时应选中当前显示器实际使用的那份。

## 开发与构建

前端 Node.js 20.19+/22.12+；后端 .NET 10 SDK。此次本机 SDK 位于 `%LOCALAPPDATA%\WallpaperDashboard\devtools\dotnet`。优先复用已安装 SDK。

```powershell
npm ci
npm run dev
npm run check
./scripts/build.ps1
```

构建只生成 `dist/` 和 `artifacts/staging/` 候选包，不停止或覆盖运行桥接。`build-info.json` 记录构建 ID、源码提交、源码是否有未提交变更及文件 SHA256；健康接口和宿主报告分别提供桥接与前端构建 ID。

```powershell
# 只验证，不安装；22 项前端测试、桥接检查、更新回退检查
./scripts/verify.ps1
# 附加匿名浏览器回归，Python 需可导入 Playwright
./scripts/verify.ps1 -Browser -Python '<python.exe 完整路径>'

# 独立更新：保留原运行方式和配置，失败恢复旧目录与运行状态
./scripts/update-bridge.ps1
# 原桥接未运行时，如需安装后启动
./scripts/update-bridge.ps1 -Start

# 只读资源观察；记录采样口径，不能直接当作受控 A/B
./scripts/measure-resources.ps1 -Samples 120 -IntervalSeconds 5 -Label after-optimization
# 24 小时只读稳定性观察；按分钟留存匿名状态和进程内存
./scripts/observe-stability.ps1
```

更新正在运行的管理员桥接时，更新脚本会弹出一次 UAC，在原用户的管理员上下文恢复计划任务；无需在构建时提升权限。候选桥接须通过健康、构建 ID、配置保留及首个硬件样本检查。旧发布目录保存在 `artifacts/backups/bridge-releases`。更新记录位于 `artifacts/validation/bridge-update.json` 和 `wallpaper-update.json`，程序验证与真实桌面点击分别记录。新安装时先启动桥接以生成配对票据。

后端源码：`bridge/WallpaperBridge/`；三组件与数据契约：`src/`；宿主属性：`wallpaper/project.json`。前端输出为经典 IIFE，避免本地文件 ES module 的跨域问题，所有背景与样式随壁纸打包。

浏览器验证使用 `tests/browser_check.py`；简洁布局与异常状态检查使用 `tests/clean_layout_check.py`，均在本机桥接启动后执行。脚本依赖 Playwright，可安装到项目 `.tools/python`，默认用已安装的 Chrome。生成截图与结果位于 `artifacts/validation/`，不进入 Git。

## 配置与凭据

管理凭据、账号选择和数据缓存存储在 `%LOCALAPPDATA%\WallpaperDashboard\private`，使用 Windows DPAPI CurrentUser 加密，目录 ACL 限制当前用户和 SYSTEM。配置页不会返回管理凭据。修改凭据只在本机设置页完成。

Codex 的 MSIX 上下文可能将 AppData 文件虚拟化至其 `Packages/.../LocalCache/Local` 目录；外部终端使用常规用户目录。建议持续从同一入口启动桥接。安装脚本优先读取当前运行桥接的展示票据，避免启动上下文差异导致壁纸连接失效。所有 PS1 使用 UTF-8 BOM，兼容 Windows PowerShell 5.1。

API 只监听 `127.0.0.1:17632`，展示数据必须有展示票据，管理接口使用独立短期会话与同源检查。地址可通过桥接 `--port` 参数及 WE 的本机数据地址调整；便捷脚本使用默认端口。客户端拒绝远端桥接地址。

展示票据会保存到私有目录以及本机已安装 WE 的 `project.json`；它只允许读规范化展示数据、报告壁纸状态及请求固定的用量刷新，不能修改配置或读取管理凭据。分享壁纸前应使用票据为空的 `dist/`，不要分享安装目录或私有配置。用户原图的使用授权由其权利人决定，本项目面向个人本机使用。

数据契约与访问边界见 [DATA-CONTRACT.md](docs/DATA-CONTRACT.md)。
