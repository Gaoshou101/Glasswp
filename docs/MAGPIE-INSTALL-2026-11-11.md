# Magpie 接入安装与实测

2026-11-11（Asia/Shanghai），用户授权“安装并测试一下 magpie 接入”后完成。

- 最终构建：`20261110161348-d8a34615`（构建 ID 使用 UTC）。
- 桥接安装目录：`E:\WallpaperDashboard\current`；自启动任务 `WallpaperDashboardBridge` 为 Running。
- 壁纸安装目录：`F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\myprojects\portrait-dashboard-workshop`。
- 仅重新应用到已核实活动的 BOE1600 / UID524 显示器。Wallpaper Engine 已上报相同构建，1600×2560、30 FPS、未暂停；没有测试物理鼠标点击。
- 复用本机正在运行的 Magpie，连接 `http://127.0.0.1:3425`；无需重新安装 Magpie 程序。
- 最终生效来源为 `magpie`；原有 12 个 CPAMP 账号与连接、天气、时钟、城市设置均保留。

## 真实接入结果

真实 `GET /v1/magpie/quotas?kept=1` 返回 HTTP 200、list envelope、2 个账号条目。

| 来源 | 实测结果 |
| --- | --- |
| Codex | Plus 正确映射为 GPT Plus；最终验收时 5 小时余量 55%、7 天余量 92%；采集时间有效、两个窗口均非 stale，重置时间和倒计时有效。数值为测试时快照，会随使用变化。 |
| WorkBuddy | Magpie 上游采集返回 HTTP 401 认证被拒，没有额度窗口。页面显示“暂无额度证据”，没有捏造额度。无法仅凭该错误确认登录过期，需在 Magpie 中处理对应账号认证。 |

整体 quota status 为 stale 是因为包含 WorkBuddy 失败条目；Codex 两个窗口均保持新鲜状态。测试仅读取 Magpie 缓存，没有发起模型推理或更改任何上游登录。

真实接口暴露窗口名称为 `5 hours` / `7 days`，已补齐这两个名称及 `24 hours` 的时长识别，并新增真实响应形状 fixture，验证百分比、套餐、时长和重置时间。

## 验证

- 1053 项桥接检查、35 项前端测试、TypeScript / Vite 构建通过。
- 发布事务 12 项检查通过；安装脚本验证完整性、配置保留和启动成功。
- 真实管理页验收 7 项通过：三来源选择与保存、Codex 映射、刷新请求、来源隔离、既有配置保留、页面重载后持久化、真实额度浏览器渲染。浏览器异常数为 0。
- CPAMP 与 Magpie 均实际切换并验证；本机自动读取仅检查选项与未保存草稿行为，本次没有重新遍历其他原生账号。
- 测试脚本首次重跑时因初始来源已是 Magpie 的断言写法失败，已修正测试并完整重跑通过；未发现对应产品问题。
- 证据：`artifacts/validation/magpie-live-20261111.json`、`magpie-live-quota.png`、`magpie-install-build.log`、`bridge-update.json`、`wallpaper-update.json`。

## 回退记录

- 最终桥接更新的直接前一版本备份：`E:\WallpaperDashboard\backups\previous-65dfbf20bd2541fbaaa71ea6df2e5426`。
- 本次首次安装前的桥接备份仍为：`E:\WallpaperDashboard\backups\previous-1043060d1872458391db340ac05db30e`。
- 最终壁纸更新备份：`F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\dashboard-backups\previous-bc11364507fe452c8497c2dce791a214`。
- 代码仍在现有工作区，未提交或推送。
