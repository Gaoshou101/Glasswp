# AI 来源配置与 CPA 直连调研

后续更新：独立 CPA/Sub2API 来源与本机/Magpie 配置菜单已于同日完成并安装，详见 [CPA 与 Sub2API 独立来源](CPA-SUB2API-SOURCES-2026-11-11.md)。下文保留最初调研阶段记录。

2026-11-11，Asia/Shanghai。代码与隔离测试已完成；本次未更新已安装桥接或桌面壁纸，未连接用户的 CPA 实例，未提交或推送工作区。

## 管理页变更

选择本机 AI Agent 或 Magpie 后，侧栏与正文显示对应的独立配置菜单。来源、连接、账号和刷新设置统一通过“保存 AI 来源”或当前来源配置区的保存按钮提交；切换选项本身不会改变生效来源。未保存的草稿可撤销，保存其他设置不丢失 AI 草稿。

- 本机与 Magpie 分别保存自动刷新开关及间隔，整数范围 1–10080 分钟，默认分别为 10 和 1 分钟。
- 关闭自动刷新后不自动查询，仍可手动读取；同一来源保留 60 秒最短手动查询间隔。
- 默认自动显示全部账号；关闭后可勾选、设置显示别名、上移/下移排序。选定模式下，新账号不自动显示；空列表表示不显示任何账号。
- 账号显示配置只影响壁纸展示，不限制原读取器扫描或查询范围；管理页仍能看到全部检测结果，便于重新选回。
- 别名/顺序/勾选修改立即应用现有快照，不额外触发上游请求，不清空额度证据。
- 切换来源保留各自配置。Magpie 更换地址时清除旧密钥及旧账号筛选，避免旧地址账号配置污染新来源。
- 管理页展示最后成功时间与下次刷新时间。密钥只写入 DPAPI 配置，不从配置接口回传。

主要实现：`AiSourceSettings.cs`、`AiQuotaSources.cs`、`DataHub.cs`、`Program.cs`、`public/admin/index.html`、`public/admin/admin.js`。

## CPA 直连结论

**源码层面可行，不需要经过 CPAMP 面板。** 但不能把现有 CPAMP 地址简单改成 CPA 地址：现有刷新流程把 Provider 用量写入 CPAMP 的 `/v0/management/quota-snapshots`，再读回 CPAMP 数据库快照。CPA 直连需要新增独立来源与本地快照处理，不调用该数据库接口。

这里的 CPA 指 `router-for-me/CLIProxyAPI`。以下是 2026-11-11 获取的上游主分支源码证据，不代表用户部署版本或最新正式发布版本已核实。

| 用途 | v0 管理接口 | v8 管理接口 |
| --- | --- | --- |
| 账号发现 | `GET /v0/management/auth-files` | `GET /v8/management/credentials` |
| 通过所选凭据代查厂商接口 | `POST /v0/management/api-call` | `POST /v8/management/requests/api-call` |
| 配额插件 | `GET/POST /v0/management/plugins/:id/quota` | `GET/POST /v8/management/plugins/:id/quota` |
| 通用配额入口 | `GET /v0/management/quota/providers`、`POST /v0/management/quota/fetch` | 已检查的 v8 路由未注册同名入口，应使用插件路由 |

`api-call` 接受 `auth_index`、固定的厂商 URL、HTTP 方法和 header；`$TOKEN$` 由 CPA 在服务端替换，不需要壁纸下载账号 OAuth 凭据。调用需要 **CPA 管理密钥**与相应远程管理权限，普通模型 API Key 不能据此视为管理密钥。必须同时检查外层 HTTP 状态和返回体内 `status_code`。

账号列表包含 `auth_index`，Codex 的 `id_token` 展示对象可包含 `plan_type`。套餐信息仍应结合厂商响应；其他 Provider 不保证都有同样的套餐字段。已有 Codex、Claude、Antigravity 配额适配器可作为直连实现基础；没有真实额度证据时应显示未知，不能用请求次数或 token 用量推算剩余套餐。

新版插件入口不是所有账号都有配额：v0 通用入口在无插件或声明式探针时返回 501；v8 账号列表仅暴露插件配额能力。应先发现实例版本、管理 API 和账号配额能力，再选择插件或固定 Provider 查询。不能假定所有 CPA 版本支持统一配额 API。

建议下一步实现独立 `cpa` 来源，包含独立地址、加密管理密钥、账号筛选和刷新配置；按能力选择插件查询，或通过 CPA 的 `api-call` 查询 Codex/Claude/Antigravity，将结果直接规范化为壁纸快照。CPAMP 请求历史功能不能因此默认视为已支持。

### 官方源码证据

- 主分支核查基准：`8bba7a77c6bcbfd993516d26606bfecaa4ff49d0`。
- [v0 管理路由](https://github.com/router-for-me/CLIProxyAPI/blob/8bba7a77c6bcbfd993516d26606bfecaa4ff49d0/internal/api/server_management.go)
- [v8 管理路由](https://github.com/router-for-me/CLIProxyAPI/blob/8bba7a77c6bcbfd993516d26606bfecaa4ff49d0/internal/api/server_management_v8.go)
- [插件配额处理及 501 分支](https://github.com/router-for-me/CLIProxyAPI/blob/8bba7a77c6bcbfd993516d26606bfecaa4ff49d0/internal/api/handlers/management/plugin_quota.go)
- [api-call 契约](https://github.com/router-for-me/CLIProxyAPI/blob/dd7b4334920b142634da2d39b525839e7d50797e/internal/api/handlers/management/api_tools.go)（该文件最近修改提交）
- [账号列表与套餐 claims](https://github.com/router-for-me/CLIProxyAPI/blob/0559fa3cfd5722cf1755a7dc3a90573ede3835f6/internal/api/handlers/management/auth_files.go)（该文件最近修改提交）

未读取或修改任何用户 CPA 账号，未发起模型推理、配额重置或账号刷新。

## 验证

- 1068 项桥接检查通过；包含来源隔离、显示过滤、别名排序、空选择、地址更换、无额外请求、关闭自动刷新及手动刷新节流。
- 37 项前端测试及 TypeScript/Vite 构建通过。
- AI 来源管理页 8 组浏览器验收通过，覆盖独立菜单、勾选/别名/排序、刷新设置、页面重载、保存失败草稿、安全文本渲染、桌面与 390px 移动布局；浏览器异常为 0。
- 原管理页 15 组浏览器回归通过；新增两块配置面板后更新面板数量断言。
- 隔离桥接 API 测试通过：配置持久化、非法输入拒绝、会话保护、隐藏账号不影响发现列表、显示更改不触发额外查询、刷新状态接口。
- Windows 默认 Python 为 3.11，而仓库 `.tools/python/greenlet` 为 cp312；浏览器测试使用已安装的 `C:/Users/Xu/AppData/Roaming/uv/python/cpython-3.12.14-windows-x86_64-none/python.exe`。
- 证据位于 `artifacts/validation/ai-settings-bridge.log`、`ai-settings-frontend.log`、`ai-sources-admin-result.json`、`ai-sources-admin-1200.png`、`ai-sources-admin-390.png`，官方源码取证为同目录 `cpa-*.go`。
