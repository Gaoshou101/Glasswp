# 运行目录迁移验收（2026-11-04）

本次已将实际运行目录从旧 OneDrive checkout 迁移到 `E:\Codex\repositories\wallpaper`，取代 `ADMIN-SETTINGS-2026-10-30.md` 中“尚未迁移”的历史状态。

- 计划任务：`\WallpaperDashboardBridge`。Action 运行 E 盘的 `scripts\install-bridge-autostart.ps1 -Run`，WorkingDirectory 为 E 盘仓库根。
- 桥接程序：`E:\Codex\repositories\wallpaper\artifacts\bridge\WallpaperBridge.exe`。
- 静态根：`E:\Codex\repositories\wallpaper\artifacts\bridge\wallpaper`。
- 私有 DPAPI 数据继续使用 `C:\Users\Xu\AppData\Local\WallpaperDashboard\private`，未复制到仓库。
- 健康端点：`http://127.0.0.1:17632/v1/health`；构建保持 `20261030144349-fb9d986f`，管理员权限保持。

## 处理与验证

E 盘原有发布包是较旧构建，并带有一份 OneDrive 冲突 DLL，不能直接切换运行。先通过 `Test-ReleasePackage` 核验旧 C 盘正在使用的发布包，再用 `Invoke-ReleaseTransaction` 将同一个已验证发布包安装至 E 盘。E 盘原发布包保存在：

`artifacts\backups\bridge-migration-20261104\previous-5803bb59d37a4432b4f834742baa8ddb`

随后仅替换既有计划任务的脚本路径和工作目录。任务的 Principals、Triggers、Settings、RegistrationInfo 对比保持一致。没有通过构建或发布操作覆盖 E 盘现有源码改动。

验收结果：

- 运行进程来自 E 盘，任务 Running，健康端点构建和管理员状态符合预期。
- `config.dpapi` 文件哈希保持一致；展示票据语义及 `read-token.txt` 哈希保持一致。
- 11 个账号的顺序和配置、刷新设置、城市及传感器选择保持一致，硬件快照可用。
- 六个 HTTP 静态文件与 E 盘实际文件的 SHA256 一致，发布清单 359 个文件通过核验。
- Wallpaper Engine 选中的 `F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\myprojects\portrait-dashboard-workshop\index.html` 及相关项目属性未改变。
- 在已检查的计划任务、服务、启动项、可读进程命令行及 Wallpaper Engine 路径中，没有旧工程根的运行引用。

启动时 PrivateStore 会重新加密展示票据，因此 `read-token.dpapi` 的密文字节变化不等同于票据变化。本次不以该密文哈希作为票据一致性条件。

## 旧工程保全与清理

67,419 个普通文件全部逐文件核对 SHA256：66,625 个在 E 盘现有副本中保全，794 个差异或独有文件保存在：

`E:\Codex\backups\wallpaper-migration-20261104T0127\legacy-only-and-different`

差异备份约 232.81 MiB，保留旧源码、Git 数据及历史产物。此本地备份不在 `E:\Codex\repositories` 的 CD2 工程同步范围内，未声称已经获得云备份。

确认运行迁移及保全通过后，已删除旧根 `C:\Users\Xu\OneDrive\文档\ChatGPT\wallpaper`：67,419 文件、7,397,110,740 逻辑字节、2 个链接及 7,438 个目录，失败数 0。删除链接时未遍历链接目标。

完整执行与独立验收证据在聊天工作区 `E:\Codex\projects\2026-11-03\wo-d\work`：`wallpaper_runtime_migration.json`、`wallpaper_runtime_acceptance.json`、`wallpaper_preservation_manifest.json`、`wallpaper_legacy_delete_result.json`、`wallpaper_migration_final_acceptance.json`。未重启 Windows；备份及自动恢复分支保留，未人为触发失败演练。
