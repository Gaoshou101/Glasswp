# 桥接安装与 README 验证 · 2026-11-12

## 交付内容

- `install-bridge.cmd` 与 `scripts/install-bridge.ps1` 统一首次安装和升级；默认启动桥接、配置登录自启动并打开设置，支持只检查、自定义路径和保留已有自启动策略。
- `scripts/package-bridge.ps1` 生成 Windows x64 自包含 ZIP 和 SHA256；完整构建自动调用。包中包含空展示票据的通用壁纸。
- README 使用三张真实界面的匿名示例 WebP，共约 265 KiB；安装和开发细节移至独立文档。

## 已完成验证

| 项目 | 结果 |
|---|---|
| 前端测试、TypeScript、Vite | 43 项测试通过，构建通过 |
| 桥接检查 | 1,111 项通过 |
| 安装编排 | 16 项通过，包括失败重试保留自定义私有目录 |
| 安装包 | 7 项通过，包括中文路径、无 BOM 中文 JSON、CMD 模块隔离、篡改与票据拒绝 |
| 发布事务 | 12 项通过 |
| 隔离真实进程回退 | 启动失败后恢复旧程序、配置和展示票据 |
| 最终 ZIP | 解压后的 CMD `-Check` 通过，全部安装入口与脚本哈希匹配工作区，ZIP SHA256 通过 |
| README 图片 | 三图目视检查；截图使用模拟 API，0 浏览器错误，0 真实 API 请求 |
| 文档 | 29 个本地文档/图片引用有效，`git diff --check` 通过 |

最终本地候选构建 ID：`20261112031319-9707a138`。安装包约 56.3 MiB，位于 `artifacts/packages`。详细机器记录为 `artifacts/validation/installer-acceptance.json`；完整构建记录为 `installer-build-final.log`，综合验证记录为 `installer-verification.log`。

## 修复的兼容性与恢复问题

1. 首次启动及停止后的升级固定原有私有目录；失败报告也保留目录，防止再次安装时退回默认目录。
2. Windows PowerShell 5.1 读取项目 JSON 时显式指定 UTF-8，支持前端生成的无 BOM 中文文件。
3. CMD 入口使用系统 Windows PowerShell，并隔离继承的模块搜索路径，避免从 PowerShell 7 启动时找不到 `Get-FileHash`。
4. 自启动注册失败时尝试恢复旧任务与运行状态；不把部分安装报告成全部成功。

## 验证边界

未更新实际桌面桥接、修改实际计划任务或发布线上版本。UAC 和任务操作使用隔离替身验证，真实系统注册仍需实际安装验收。普通用户不能读取提权进程信息时，`-Check` 明确返回 `ownershipNeedsElevation=true`，实际安装在提权后再次核验。

重复完整构建期间，原有 Windows 原生窗口事件检查曾一次失败于 `destroyed top-level window release is blocked at original position`；随后完整构建通过。本次未修改窗口事件实现或放宽断言；该偶发现象仍需独立追踪。
