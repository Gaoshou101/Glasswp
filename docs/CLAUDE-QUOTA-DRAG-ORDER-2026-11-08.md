# Claude 用量获取与账号拖拽排序（2026-11-08）

本轮完成两项：桥接为 Claude 账号自动获取额度窗口与套餐；管理页「账号」列表支持用鼠标（或触屏）拖动调整壁纸显示顺序。代码尚未安装到正式运行目录，见文末「部署」。

## Claude 用量与套餐

### 请求

与 CPAMP v1.14.10（`providerRequests.ts`）一致，经 CPA `POST /v0/management/api-call` 按账号 `authIndex` 代为附加 OAuth 令牌，桥接不接触 Claude 凭据：

| 用途 | 方法与地址 | 请求头 |
|---|---|---|
| 用量 | `GET https://api.anthropic.com/api/oauth/usage` | `Authorization: Bearer $TOKEN$`、`Content-Type: application/json`、`anthropic-beta: oauth-2025-04-20` |
| 套餐 | `GET https://api.anthropic.com/api/oauth/profile` | 同上 |

先读用量；用量失败（如 401/403/429）不再请求资料接口，也不写入快照。

### 窗口

| 上游字段 | `provider_window_id` | 周期 | 壁纸标签 |
|---|---|---|---|
| `five_hour` | `five-hour` | 5 小时 | 5 小时窗口 |
| `seven_day` | `seven-day` | 7 天 | 周窗口 |
| `seven_day_oauth_apps` | `seven-day-oauth-apps` | 7 天 | 周窗口 · OAuth 应用 |
| `seven_day_opus` | `seven-day-opus` | 7 天 | 周窗口 · Opus |
| `seven_day_sonnet` | `seven-day-sonnet` | 7 天 | 周窗口 · Sonnet |
| `seven_day_cowork` | `seven-day-cowork` | 7 天 | 周窗口 · Cowork |
| `iguana_necktie` | `iguana-necktie` | 7 天 | 周窗口 · Iguana Necktie |
| `limits[]` 模型周额度 | `weekly-scoped-id-<模型 ID>` | 7 天 | 周窗口 · <模型 ID> |
| `extra_usage`（已开启） | `extra-usage` | 非周期 | 额外用量 |

- 顶层字段需含 `utilization` 键；用量或重置时间至少一项可读才保留。`utilization` 为已用百分比；数值型重置时间小于 1e12 视为秒，否则为毫秒，ISO 字符串按 UTC 解析。
- 顶层 5 小时或 7 天窗口缺失时，回退到 `limits[]` 中未停用、无 `scope` 的 `session`（5 小时）与 `weekly`/`weekly_all`（7 天）项；多项时依次取较晚重置、信息更完整、`weekly_all`、更高用量。
- `limits[]` 中 `weekly_scoped`、`weekly_model_scoped` 或分组为 `weekly` 的 `model_scoped` 项成为模型周额度，插在 `seven_day` 之后并按名称排序；与带模型 ID 项等价的纯名称重复项被合并。无模型 ID 的项按 CPAMP 规则写为不完整范围（`feature` / `scope_unknown`，周期与边界精度 `unknown`），不会被当作全账号额度。
- 额外用量的金额单位为美分：写入 `used_value`/`limit_value`（美元）与 `quota_unit: USD`；无 `utilization` 时按 `used_credits / monthly_limit` 计算百分比。窗口类型 `billing`、模式 `non_window`，不参与「用尽至重置」判断。
- CPAMP 不保存窗口标签，壁纸读取快照时按 `provider_window_id` 和 `model_ids` 重建标签；模型周额度显示模型 ID（CPAMP 网页显示的模型展示名不会写入快照）。没有模型 ID 的窗口 ID 来自小写名称，显示时把每个单词首字母恢复为大写（`weekly-scoped-fable` → 「周窗口 · Fable」），写入 CPAMP 的窗口 ID 不变。

### 写回 CPAMP

可读的用量响应即为该账号当前完整清单：以 `inventory_mode: complete`、`inventory_scope_key: claude:quota-windows` 写入 `/v0/management/quota-snapshots`，上一轮存在、本轮消失的窗口由 CPAMP 退役。所有窗口共用一个观察时间；响应中 `row_key` 与写入数量须与请求一致，否则视为失败并保留上次快照。

### 套餐

资料接口判定：`has_claude_max` → `plan_max`；`has_claude_pro` → `plan_pro`；组织类型 `claude_team` 且订阅 `active` → `plan_team`；两项均明确为否 → `plan_free`；其余情况不判定。原始键随窗口写入 `plan_type`，壁纸与管理页显示为 `Claude Free`、`Claude Pro`、`Claude Max`、`Claude Team`（亦识别 `plan_max5`/`plan_max20` 等别名）。

套餐读取是尽力而为：成功后缓存 1 小时；失败（含 429）不影响用量写入，10 分钟后再试，期间沿用 1 天内的已知套餐；从未读到时不写 `plan_type`（CPAMP 会沿用较早快照里的套餐），绝不猜测为 Free。未解析 `rate_limit_tier`，Max 5x/20x 档位仅在上游直接给出对应键时显示。

### 失败与边界

- 鉴权失败、限流、超时、响应不可读：该账号显示失败原因，保留上次快照，不泄露上游原始响应。
- 用量响应没有任何可读窗口时视为不可读，不写入空清单。
- 其他平台（Kimi、xAI 等）仍显示「该平台暂不支持自动刷新」。
- 当前正式配置中没有关注的 Claude 账号，本轮未做真实上游刷新；解析规则以 CPAMP v1.14.10 源码为准，并由隔离测试覆盖。

## 账号拖拽排序

- 每个已关注账号行首新增拖动手柄（六点图标）。按住手柄拖动，所在行浮起跟随指针，其余可见的已关注账号让出位置；松开即调整草稿顺序，与上移/下移一样标记「未保存」，保存后生效。
- 使用 Pointer Events，鼠标、触屏与触控笔通用；手柄设置 `touch-action: none`，不会触发页面滚动。拖动中按 Esc、指针被系统取消或列表被重新渲染时，放弃本次拖动并让各行回到原位。
- 指针停在窗口上下边缘 80 px 内时页面自动滚动，长列表可一次拖到底部或顶部。拖动只在已关注账号范围内移动，不会拖进未关注账号区。
- 筛选时与上移/下移语义一致：只在可见的已关注账号之间调整，放在目标可见邻居之前或之后，隐藏账号保持原有相对顺序。
- 未关注行不显示手柄；保存中或可见已关注账号少于两个时手柄禁用。在别名输入框里按下并拖动只会选择文字，不会开始排序。
- 手柄仅供指针操作（对读屏隐藏），键盘与读屏用户继续使用带名称的上移/下移按钮。放下与取消时的归位动画在「减少动态效果」下自动关闭。
- 设置页说明、筛选提示和「用量自动刷新」的支持平台文字已同步更新。

## 验证

- `scripts/verify.ps1 -Browser -Python <Python 3.12>`：退出码 0。
  - 前端 26/26 测试、TypeScript 与 Vite 构建通过。
  - 桥接 323 项检查通过（此前 270）。新增 `ClaudeChecks`：窗口解析与排序、秒/毫秒重置、模型周额度合并与不完整范围、额外用量、`limits` 回退、不可读响应、套餐判定与名称、壁纸标签重建、自动刷新请求头与身份、完整清单写入、套餐 1 小时缓存、资料接口 429 不影响用量、10 分钟重试、用量 429 不写入且不泄露原文、无套餐时不写 `plan_type`。原测试中「不支持的平台」改用 Kimi。
  - 发布检查与 6 组浏览器回归通过；`admin_settings_check.py` 15 组（新增拖拽：浮起与让位、Esc 取消、向下/向上拖动、边缘自动滚动、筛选语义、别名框不触发、单账号禁用、未关注行隐藏手柄；360/540/900/1440 宽度检查纳入手柄）。
- `git diff --check`：通过。
- 截图：`artifacts/validation/admin-settings-accounts-dragging.png`（拖动中）、`admin-settings-accounts.png`、`admin-settings-accounts-mobile.png`。
- 本机 PATH 中的 `python` 为 3.11（WindowsApps 别名），而 `.tools/python` 中的 Playwright 依赖为 cp312 编译；浏览器回归需用 `-Python` 指定 Python 3.12 解释器。

## 部署

桥接与管理页一同随发布包安装：先 `scripts/build.ps1` 生成 `artifacts/staging`，再运行 `scripts/update-bridge.ps1`（一次 UAC 确认，安装后核对哈希与权限）。

2026-11-08 20:02 经用户同意完成正式安装：

- 干净构建 `20261108115756-c24fc579`（提交 `e8e4fcb`，`dirty=false`）替换 `20261108064802-3b4716a8`；回退副本在 `E:\WallpaperDashboard\backups\previous-92392b96d54b4b42984c7c262c078e18`。更新脚本完成健康检查、构建 ID 与私有配置一致性核对；桥接仍以管理员身份由计划任务运行。
- 运行中的 `/admin/admin.js` 与 `/admin/index.html` 与发布清单哈希一致。只读浏览器验收（拦截全部写入接口，实际无拦截请求、无浏览器错误）：12 个关注账号均有可用手柄；Esc 取消后顺序不变，放下后前两项互换并标记未保存，撤销后恢复保存顺序；刷新说明已含 Claude。
- 首次自动刷新 11/12 成功。Claude 账号「CC」写入 3 个窗口：5 小时（剩余 86%）、周窗口（剩余 55%），以及一个仅有名称、没有模型 ID 的模型周额度（显示「周窗口 · fable」，按不完整范围记录）；套餐显示 `Claude Max`。另一个失败账号是已有的 Codex 账号，其快照停在安装前一天（2026-11-07 19:45），与本次改动无关。
- WE 壁纸前端（`wallpaper.js`/`wallpaper.css`）本轮未改动，无需重装壁纸包。
- 证据：`artifacts/validation/bridge-update.json`、`live-admin-drag-check.json`、`live-claude-read.json`。

## 本机显示映射（2026-11-08 追加）

按用户要求，只改本机壁纸与管理页的显示，写入 CPAMP 的原始数据保持不变：

- 只有名称、没有模型 ID 的模型周额度，标签恢复首字母大写：「周窗口 · fable」→「周窗口 · Fable」。
- Codex 套餐按 CPAMP v1.14.10 `plans/providers/codex.ts` 的键映射为 GPT 套餐名：`free`→GPT Free、`go`→GPT Go、`plus`→GPT Plus、`prolite`/`pro_5x`→GPT Pro 5x、`pro`/`pro_20x`→GPT Pro 20x、`team`→GPT Team、`business`→GPT Business、Business 5x / PAYG、`enterprise`/`ent26`/`hc`→GPT Enterprise、Ent. Auto / Ent. PAYG、`edu`/`edu_plus`/`edu_pro`→GPT Edu / Edu Plus / Edu Pro；不区分大小写，未知值原样显示，已映射的名称再次映射不变。
- 映射发生在桥接读取快照（`QuotaAdapter.Parse`）和本机响应头缓存（`CachedHeaderAdapter.ParseCodex`）时；刷新写回 CPAMP 的 `plan_type`（`plus`、`team`、`plan_max` 等）与窗口 ID 不变。Antigravity（Google AI Pro 等）不受影响。