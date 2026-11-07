# 管理员桥接迁入受保护运行目录（2026-11-07）

本次把管理员计划任务启动的桥接从开发仓库移到受保护运行目录 `E:\WallpaperDashboard`，取代 [2026-11-04 运行目录迁移](RUNTIME-MIGRATION-2026-11-04.md)中“计划任务执行仓库脚本”的状态。

## 原问题

计划任务以管理员权限执行 `E:\Codex\repositories\wallpaper\scripts\install-bridge-autostart.ps1 -Run`，再启动仓库内的 `artifacts\bridge\WallpaperBridge.exe`：

- 仓库对普通权限进程可写，任何以当前用户运行的程序都能改写这两处，下次登录时以管理员权限执行。
- 开发改动、切换分支、清理 `artifacts` 或同步冲突会直接影响生产；运行目录位于 CD2 工程同步范围内，此前旧发布包曾因 OneDrive 冲突 DLL 失效。
- 仓库位于用户可完全控制的 `E:\Codex` 之下，即使只收紧子目录权限，上级目录仍可被改名替换。

## 现在的布局

| 路径 | 内容 |
| --- | --- |
| `E:\WallpaperDashboard\current\` | 正在运行的发布，含 `run-bridge.ps1`（发布包内哈希校验的启动脚本） |
| `E:\WallpaperDashboard\backups\` | 回退副本，更新成功后保留最近两份 |
| `E:\WallpaperDashboard\state\` | 启动核验、更新、迁移记录及迁移前后的任务定义 |
| `%LOCALAPPDATA%\WallpaperDashboard\private` | DPAPI 私有配置，位置未变 |

运行根直接位于盘符根下（`E:\` 所有者为 SYSTEM，普通用户没有删除子项、改权限或取得所有权的权限），不继承权限，所有者为 Administrators，DACL 为 `SYSTEM:F`、`Administrators:F`、`Users:RX`，由所有子项继承。它不在 CD2 工程同步范围内。

计划任务只更换了动作：`powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "E:\WallpaperDashboard\current\run-bridge.ps1" -DataDirectory "...\private"`，工作目录为 `current`。账号、登录后 15 秒触发、最高权限、无运行时限、失败每分钟重试三次等保持不变。启动脚本在调用任何 cmdlet 前把 `PSModulePath` 限制为 `$PSHOME\Modules`，避免以管理员身份加载用户“文档”下可写的模块。

## 脚本变化

- `scripts/release-common.ps1`：运行根、权限核验（根目录不继承、所有者、父目录不允许他人替换、每个子项无非管理员写权限、无链接）、权限初始化、回退副本保留、任务动作、配置比较与就绪等待，以及 .NET SDK 查找。
- `scripts/bridge-launcher.ps1`：随发布包复制为 `run-bridge.ps1`，状态写入 `state\autostart-runtime.json`。
- `scripts/update-bridge.ps1`：始终通过一次 UAC 安装；在受保护副本上重新核验哈希与权限；进程路径须为 `current\WallpaperBridge.exe`；失败恢复旧发布；报告复制到 `artifacts/validation/bridge-update.json`。
- `scripts/install-bridge-autostart.ps1`：新装时注册受保护启动脚本；已删除旧的 `-Run` 路径。
- `scripts/migrate-bridge-runtime.ps1`：本次一次性迁移，失败时重新注册导出的原任务并启动原桥接。
- `scripts/build.ps1`、`scripts/verify.ps1`：在 Codex MSIX 容器之外也能找到 `%LOCALAPPDATA%\Packages\OpenAI.Codex_*\LocalCache\Local\WallpaperDashboard\devtools\dotnet` 下的 SDK。

## 验证与迁移

- 提交 `a46bb65` 上 `verify.ps1`：25 项前端测试、270 项桥接检查、发布事务及新增运行权限检查通过，Windows PowerShell 5.1 与 PowerShell 7 均通过发布检查。
- 干净候选 `20261107084128-210b5852`（`dirty=false`，360 个文件，含 `run-bridge.ps1`）；隔离端口 17634 的启动失败回退演练通过。
- 首次构建时 `DesktopWindowEventChecks` 的“移动后关闭到托盘”检查因真实窗口事件计时失败一次，随后连续三次及重跑构建均通过，属于高负载下的计时敏感测试。
- 16:46:37–16:47:34 执行迁移（一次 UAC）。核验新桥接构建、管理员权限、进程路径、11 个账号与顺序、展示票据、刷新设置、城市、传感器、CPAMP 连接及首个硬件样本；启动脚本核验成功；任务账号、触发器与设置不变。
- 以普通权限独立复核：健康接口为新构建且为管理员；任务 Running，动作和工作目录指向运行根；`Assert-ProtectedRuntime` 通过 372 项；在运行根和 `current` 新建文件、以写方式打开 `run-bridge.ps1` 均被拒绝。WE 正式竖屏随后自动重连，16:50:47 上报 1600×2560、30 FPS、未暂停。
- `read-token.dpapi` 密文变化是桥接启动时重新加密所致；票据值比对一致，`config.dpapi` 未变。

记录：`E:\WallpaperDashboard\state\migration-20261107T084637Z.json`（副本 `artifacts/validation/bridge-runtime-migration.json`）、`task-before-20261107T084637Z.xml`、`task-after-20261107T084637Z.xml`、`autostart-runtime.json`。

## 日常操作

```powershell
./scripts/build.ps1            # 只生成 artifacts/staging 候选
./scripts/update-bridge.ps1    # 一次 UAC，安装到 E:\WallpaperDashboard\current 并经原任务重启
```

回退：`update-bridge.ps1` 失败时自动恢复；需要手动回到上一版本时，以管理员停止任务，用 `backups\previous-*` 替换 `current` 后启动任务。若要回到迁移前的仓库启动方式，需要先取回提交 `a46bb65` 中带 `-Run` 的 `install-bridge-autostart.ps1`，再以管理员导入 `task-before-*.xml`；不建议这样做。

## 残余风险

- 私有配置目录仍归当前用户可写，管理员桥接会解析其中的 DPAPI 配置；以当前用户身份运行的恶意程序可以篡改配置内容（不能直接替换管理员执行的代码）。DPAPI 与回环接口仍不是抵御已获授权本机恶意进程的边界。
- 仓库中的 `artifacts\bridge` 已不再被任务或进程使用，暂作参考，清理需另行确认。
- 本次未重启 Windows；登录自启动路径已通过计划任务实际启动验证，下次登录可再确认一次。