# CPA 与 Sub2API 独立来源

2026-11-11，Asia/Shanghai。用户确认 Sub2API 的目标是**后台托管的 Codex / Claude 等上游账号余量**，不是站内用户订阅或余额。

本次已完成代码与隔离验证，并在用户授权“安装吧”后安装到运行中的桥接/桌面壁纸；未连接用户的真实 CPA 或 Sub2API，未提交或推送。此前 `AI-SOURCE-SETTINGS-CPA-2026-11-11.md` 中“CPA 尚未实现”的状态由本记录更新。

## 安装验收（2026-11-11 21:15，Asia/Shanghai）

- 构建 `20261111131229-75cea152` 已安装到 `E:\WallpaperDashboard\current`，桥接以管理员权限运行，自启动任务 `WallpaperDashboardBridge` 为 Running。
- 桌面安装目录为 `F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\myprojects\portrait-dashboard-workshop`；仅重新应用到已核实的 BOE1600 / UID524。
- Wallpaper Engine 上报相同构建号、1600×2560、30 FPS、未暂停；未验证物理鼠标点击。
- 安装前后的既有配置摘要一致，12 个 CPAMP 账号、连接、显示令牌、天气、时钟、城市、传感器和壁纸属性均保留；当前来源保持安装前的 `cpamp`。
- 实际管理页五来源选项、四个独立配置面板、未保存草稿撤销验证通过，浏览器异常为 0；未保存新服务地址、密钥或切换实际来源。
- 安装包构建再次通过 1111 项桥接检查、37 项前端测试与构建，12 项发布事务检查通过。
- 桥接回退目录：`E:\WallpaperDashboard\backups\previous-ce6a2046f82d4de0bac07814716f7341`。
- 壁纸回退目录：`F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\dashboard-backups\previous-53df6c79cd6140738150910f3b7d72ae`。
- 验收记录：`artifacts/validation/managed-sources-installed.json`、`managed-sources-install-build.log`、`bridge-update.json`、`wallpaper-update.json`。

## 使用

管理页“AI 套餐来源”新增 `CPA`、`Sub2API`。选择后显示各自的独立配置菜单：

| 来源 | 连接配置 | 已实现的额度读取 |
| --- | --- | --- |
| CPA | 服务根地址、CPA 管理密钥、v0/v8 管理接口版本 | 优先使用账号声明的配额插件；没有插件时查询 Codex、Claude、Antigravity 的固定厂商额度接口 |
| Sub2API | 服务根地址、管理员 API Key | Codex / Claude / Antigravity 的 OAuth 托管账号；不把站内消费限额或 Setup Token 状态估算映射为真实套餐余量 |

两者都支持自动刷新开关、1–10080 分钟间隔（默认 10 分钟）、手动读取、显示账号选择、别名和上移/下移排序。默认显示所有发现的账号，未支持的账号类型保留为无余量证据。显示筛选不限制扫描范围。

填写根地址，不附加管理 API 路径。远程只允许 HTTPS，本机回环地址支持 HTTP。普通模型调用 API Key 不是管理密钥；Sub2API 使用 `x-api-key` 管理认证，不支持在此处填写用户名密码或自动登录。建议使用具备账号列表及额度读取权限的受限管理员 Key。

切换来源保留各自配置；修改一个来源的地址/密钥不会修改其他来源。更换 CPA/Sub2API 地址或密钥会清除该来源的账号筛选，防止把旧身份绑定到新连接。密钥 DPAPI 加密保存，设置接口仅返回是否配置；浏览器保存后清空密码框。

## 接口与数据边界

CPA：

- v0：`GET /v0/management/auth-files`，`POST /v0/management/api-call`。
- v8：`GET /v8/management/credentials`，`POST /v8/management/requests/api-call`。
- 配额插件：在所选管理前缀下请求 `GET /plugins/:id/quota?auth_index=...`。缺失插件接口的 404/501 可回退到已实现的原生 Provider；401/403/429 不盲目改走其他接口。
- `api-call` 的 OAuth `$TOKEN$` 替换在 CPA 服务端完成。检查外层 HTTP 状态及内层 `status_code`。不调用 CPAMP `quota-snapshots`，不下载认证文件。
- Codex 读取真实用量及重置次数；Claude 补充 profile 套餐；Antigravity 补充 `loadCodeAssist` 付费等级。套餐查不到时不猜测 Free/Pro。
- 统一插件 `remainingFraction` 按 0–1 映射为百分比，缺失/非法值不视为零。当前实现消费插件 `subscription` 与 `groups`；任意插件自定义 `summary` 指标不推断为余额。

Sub2API：

- 账号列表：`GET /api/v1/admin/accounts?page=...&page_size=100&lite=true`，校验分页和 ID，当前最多 200 个账号；超出上限或分页不完整明确读取失败，不静默截断。
- Codex：`GET /api/v1/admin/openai/accounts/:id/quota`。**不调用 Codex 通用 `/accounts/:id/usage`**，因为核查版本该路径可能生成 `/responses` 探测。也不调用 `/quota/refresh` 或 `/reset-quota`。
- Claude / Antigravity OAuth：`GET /api/v1/admin/accounts/:id/usage?source=active`，保留 `updated_at`、真实百分比与重置边界；不读取站内用户订阅、账单或消费统计来推算额度。
- Antigravity 分组额度与逐模型额度是替代表示，有分组时不重复叠加逐模型窗口。
- 查询行为仍受服务端实现约束：核查版本的普通管理员 Codex GET 查询会通知已有自动重置策略；受限管理 Key 走独立受限分支。本适配器不发送重置请求，不能据此保证服务端已有自动策略不会执行。Claude 主动查询也可能更新 Sub2API 自身的额度缓存。
- 不支持的上游类型、被拒绝的查询、没有额度证据的账号，不显示虚构的 100% 余量。

共同边界：响应体最多 4 MiB、单来源查询预算 2 分钟、最多 4 个账号并发查询、每个 HTTP 请求沿用 15 秒超时；手动请求保留每来源 60 秒冷却。来源/连接变化取消旧查询，丢弃过时结果，避免旧账号或刷新报告进入新来源。未知与过期证据明确标记；错误说明不输出服务端原始响应或密钥。数据仍进入现有壁纸 `QuotaData` 契约。独立来源没有自动获得 CPAMP 请求历史功能。

## 官方源码核查

本次针对以下主分支快照核查，不等同于已验证用户部署版本：

- CPA：`8bba7a77c6bcbfd993516d26606bfecaa4ff49d0`。
  - [v0 管理路由](https://github.com/router-for-me/CLIProxyAPI/blob/8bba7a77c6bcbfd993516d26606bfecaa4ff49d0/internal/api/server_management.go)
  - [v8 管理路由](https://github.com/router-for-me/CLIProxyAPI/blob/8bba7a77c6bcbfd993516d26606bfecaa4ff49d0/internal/api/server_management_v8.go)
  - [插件配额结构](https://github.com/router-for-me/CLIProxyAPI/blob/8bba7a77c6bcbfd993516d26606bfecaa4ff49d0/sdk/pluginapi/types.go)
- Sub2API：`530c5e3a4f9c73ca2b0881191d0ef8e0d82c5484`。
  - [管理认证](https://github.com/Wei-Shaw/sub2api/blob/530c5e3a4f9c73ca2b0881191d0ef8e0d82c5484/backend/internal/server/middleware/admin_auth.go)
  - [账号额度服务](https://github.com/Wei-Shaw/sub2api/blob/530c5e3a4f9c73ca2b0881191d0ef8e0d82c5484/backend/internal/service/account_usage_service.go)
  - [Codex 配额接口](https://github.com/Wei-Shaw/sub2api/blob/530c5e3a4f9c73ca2b0881191d0ef8e0d82c5484/backend/internal/handler/admin/openai_oauth_handler.go)
  - [管理路由](https://github.com/Wei-Shaw/sub2api/blob/530c5e3a4f9c73ca2b0881191d0ef8e0d82c5484/backend/internal/server/routes/admin.go)

官方源文件留存于 `artifacts/validation/cpa-*.go` 和 `artifacts/validation/sub2api-*`，不包含用户真实服务的响应。

## 验证结果

- 1111 项桥接检查通过：v0/v8 路由、插件/原生额度、Claude/Antigravity 套餐、身份命名空间、认证头隔离、错误和重定向处理、分页、真实零值、过期时间、来源切换取消旧查询。
- 37 项前端测试与 TypeScript/Vite 构建通过。
- AI 来源管理页 9 组浏览器验收通过，含新增来源的保存、重载、版本选择、独立账号设置、修改地址/清除密钥和 1200/390px 布局；浏览器异常为 0。
- 原管理页 15 组回归通过。
- 隔离桥接 API 端到端测试通过，真实本机 HTTP fixture 模拟 CPA/Sub2API：Codex Plus 与余量映射、只查询当前来源、密钥不回传、显示过滤、手动冷却、地址变更清除旧身份。没有使用用户实际管理密钥或上游登录。

验证记录：`artifacts/validation/managed-sources-bridge.log`、`managed-sources-frontend.log`、`ai-sources-admin-result.json`、`cpa-settings-1200.png`、`sub2api-settings-390.png`；测试入口为 `ManagedQuotaChecks.cs`、`ai_sources_admin_check.py`、`ai_sources_api_check.py`。
