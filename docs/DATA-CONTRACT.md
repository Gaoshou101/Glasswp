# 本机桥接数据契约 v1

地址：`http://127.0.0.1:17632`。所有响应 `Cache-Control: no-store`。本机管理接口和持展示票据的壁纸可发起固定的 Codex 用量查询；接口不支持通用代理、账户管理或执行系统命令。

## 展示接口

| 路径 | 方法 | 访问 |
| --- | --- | --- |
| `/v1/health` | GET | 服务名、版本、构建 ID、契约版本、是否管理员及输入保护可用状态 |
| `/v1/snapshot` | GET | `X-Wallpaper-Token` 展示票据 |
| `/v1/accounts/{id}/activity` | GET | 同一展示票据，只读取已配置关注账号；默认 7 天，可切换 CPAMP 全部时间范围，每页 20 条脱敏请求 |
| `/v1/client` | POST | 同一展示票据，有限的前端构建 ID、壁纸尺寸/FPS/暂停/材质诊断 |
| `/v1/desktop-input` | POST | 同一展示票据，输入 `{x,y,width,height}`，返回 `{available,allowed}`；验证鼠标位置是否命中未被遮挡的桌面 |
| `/v1/quota-refresh/status` | GET | 同一展示票据，只读刷新状态及最新快照时间 |
| `/v1/quota-refresh` | POST | 同一展示票据，只能对已保存关注账号排队固定用量刷新 |

`snapshot` 顶层：`schemaVersion:1`、`generatedAt`、`hardware`、`quota`、`weather`。时间采用含时区的 ISO 8601，客户端以配置时区格式化；倒计时按绝对时间计算。

可选 `revisions:{epoch,hardware,quota,weather}` 用于跳过未变化模块的派生计算：`epoch` 在每次桥接启动时生成，三个整数随对应模块写入递增，包含状态变化。`generatedAt` 是整份快照的读取时间，不能作为模块是否更新的依据。旧桥接缺少修订号时，前端比较模块内容；倒计时、过期判断与自动翻页仍按本地时钟执行。此扩展保持 v1 兼容。

`health.buildId` 是编译进桥接的构建标识；`client.buildId` 是嵌入前端产物的标识，仅允许字母、数字、点与短横线，最多 80 字符。二者可与发布清单交叉核对；宿主报告本身不证明物理点击已通过。`config/read.diagnostics.cacheFaults` 仅含允许的可重建缓存名及 `read/write` 故障码，不含异常正文；配置和票据读取保持严格保护。

`desktop-input` 在收到点击时调用 Windows `GetCursorPos/WindowFromPoint`，命中桌面所在的 Explorer `Progman/WorkerW` 根窗口才允许点击。不注册全局输入钩子，不轮询；只在内存保留最近一次检查，不记录窗口标题、应用内容或历史点击。坐标按显示器归一化以兼容混合 DPI 和负坐标。网页在 WE 内对孤立 `mouseup` 和原生鼠标 `click` 都调用检查，审批前抑制按压反馈；浏览器预览和键盘点击保持原行为。桥接不可用或未升级时禁止 WE 鼠标点击。

窗口最小化会先于壁纸 `mouseup` 处理完成，届时 `WindowFromPoint` 已命中桌面。为防止这类操作误触，桥接订阅 Windows `EVENT_SYSTEM_MINIMIZESTART` 窗口事件，暂存最小化时鼠标坐标，800 毫秒内对同位置（12 物理像素内）的壁纸点击拒绝；其他位置不受影响。检查前等待 20 毫秒，让同次操作的窗口通知到达。不使用鼠标输入钩子或轮询。

关闭窗口或关闭到托盘也可能产生上述松开转发。桥接另外监听 `EVENT_OBJECT_SHOW/HIDE/DESTROY/LOCATIONCHANGE`，启动时枚举可见应用窗口，并在显示或移动时更新窗口边界；隐藏或销毁时，仅当鼠标位于原窗口范围内才启用同样的 800 毫秒／12 物理像素保护。只接受顶层有标题栏或 `APPWINDOW` 样式的应用窗口，忽略子控件、工具窗口、菜单和普通弹出层，范围外的后台窗口消失不拦截点击。`health.closeGuardAvailable` 表示这些订阅可用；管理诊断的 `lastWindowDisappearance` 仅包含最近一次事件的时间、坐标和 `hidden/destroyed` 类型，`desktopInput.windowTransition` 表示此次点击是否命中最小化或关闭保护，不含应用名和窗口标题。

`hardware`：`status`、`observedAt`、`message`，四个 `cpu/gpu/vram/ram`。每个设备含 `name`、`load/temperature`（`value/unit/source/sensorId`）、`usedBytes/totalBytes`。null 为未知，0 为真实零。容量统一为字节；UI 显示 GiB 数值（GB 标签）。RAM 为 Windows 可用物理内存口径。RAM 温度已取消采集和展示，兼容 v1 的 `ram.temperature.value` 始终为 null；传感器选择仅保留 GPU ID、CPU/GPU 温度。专用显存不含共享内存，不使用 WMI AdapterRAM。所用 LHM 0.9.6 的 GPU SmallData 字段为 MiB，转换为字节。

`quota`：`status`、`fetchedAt`、`message`、`accounts`。账号仅返回用户别名、provider、plan 和展示窗口；不返回物理凭据文件路径、账号身份字段、原始响应或管理凭据。

`accounts` 按加密配置中的关注账号顺序输出；管理页上移/下移并保存后，卡片、轮播及重启后的缓存展示遵循此顺序。调整顺序不重置用量刷新计划。

每个 Codex 账号可选 `resetCredits:{available,observedAt,stale}`，表示上游确认的**当前可用重置次数**，不是历史重置次数或刷新次数。正常 Codex 用量刷新把 `rate_limit_reset_credits.available_count` 写入原配额快照；读取时采用 `reset_credits_available` 的独立字段时间，超过 15 分钟或证据冲突显示未知。真实 0 保留，不累加各窗口的重复次数；非 Codex 隐藏。旧缓存缺少该字段仍兼容。

`activity` 响应：`accountId/status/fetchedAt/from/to`、`requests/successRate/tokens/estimatedCost/lastActivity`、`recent/hasMore/message`，以及 `nextBeforeMs/nextBeforeId/totalCount` 分页字段。成功率为 0–100（无请求时 null），费用为 CPAMP 预估 USD，不能等同于真实账单。`recent` 按时间倒序，每页最多 20 项，各项仅含 `time/model/success/statusCode/tokens/inputTokens/outputTokens/cachedTokens/durationMs/ttftMs`；缺失保持 null，0 保留，成功状态不虚构 HTTP 200。`ttftMs` 来自上游 `ttft_ms`（首字响应时间），`durationMs` 来自 `latency_ms`（请求总耗时），均为毫秒，独立保留，缺失首字不从总耗时推算；前端以秒显示“首字 / 耗时”，旧桥接省略 `ttftMs` 仍兼容。没有请求时统计可为真实 0，没有接口/鉴权失败时保持未知。

查询参数 `range` 支持与 CPAMP 请求监控相同的 `today/yesterday/7d/14d/30d/all/custom`，省略默认 `7d`。`timeZone` 为客户端本地 IANA 时区，与 CPAMP 浏览器口径一致；省略按桥接本机时区。今天从本地零点开始，昨天为上一个自然日；7/14/30 天从今天零点往前 6/13/29×24 小时到现在，7 天并非最近 168 小时。全部从 Unix 毫秒 1 开始。自定义传 `fromMs/toMs`，开始须早于结束；不存在的范围、非法时区或游标返回 400。

较早页传接口返回的 `nextBeforeMs/nextBeforeId` 为 `beforeMs/beforeId`，同时固定第一页的 `fromMs/toMs`；相同毫秒的请求用 ID 区分。返回较新页沿用固定范围，点击“更新活动”回到最新第一页。页面每次仅保留当前 20 条，历史页不自动跳回最新；总览的最后活动保持第一页时间。响应只含聚合计数与显示字段，分页 ID 不作为账号身份或访问凭据。

活动请求在桥接内部固定调用 CPAMP `/v0/management/monitoring/analytics`，同时限定已配置的物理凭证文件与平台，并包含失败请求；不接受客户端传入上游地址、凭证筛选或任意账号。返回的请求行再次校验凭证身份，原始文件名、身份、API Key 哈希、会话 ID、访问 Token 指纹、请求/响应正文、任意错误正文不返回展示端。账号、范围、时区、起止及游标分别缓存：成功/空结果 60 秒，失败短暂节流；最多 128 项，移除旧条目。范围连接失败不会使用其他范围的统计；配置变更清空缓存，丢弃旧来源的在途结果。账号未选中返回 404，错误票据/远端 Origin 拒绝。

前端只在该账号浮层打开时读取活动，最新第一页沿用生命周期时钟每 60 秒更新；点击“更新活动”复用桥接缓存。关闭、暂停、换范围或换账号取消在途请求，迟到的旧账号/旧范围响应不会覆盖新页面。选择范围仅在本次运行保留，初始默认 7 天。读统计不查询 Provider 新额度，也不执行重置；真实额度刷新继续由原有刷新任务负责。

展示端刷新接口返回 `canRefresh`、`refresh`（与管理页相同的规范化刷新状态）、`snapshotFetchedAt`。请求合并到同一个桥接刷新任务；自动刷新关闭时仍可手动请求。前端等待新快照时间不早于刷新完成时间才显示“已刷新”。展示票据仍无法访问任何 `/v1/setup` 配置接口；远端 Origin、错误票据拒绝，`file://` 使用 Origin null。

窗口：`id/label`、`remainingPercent`、`usedValue/limitValue/quotaUnit`、`resetAt/durationSeconds`、`mode/accuracy`、`observedAt/stale`。不合并或平均不同模型/账号/周期。`used_percent` 可换算余量，明确缺失的字段保持 null。

前端可选 `ignoreCodexBaseWeekly`（WE属性 `ignore_codex_base_weekly`），默认false。仅过滤明确识别为Codex基础模型/luna-release的周窗口；主套餐周窗口不受影响。过滤在分页及新鲜度计算前进行，原始快照和上游刷新保持完整，取消过滤可恢复窗口。

CPAMP 读取账号列表和只读快照查询，按物理文件与成员身份匹配。Codex 需要工作空间账号 ID 与规范化成员邮箱；冲突成员证据不选取任何一个。快照的 `field_sources.quota.observed_at_ms` 用于余额新鲜度。响应头缓存只使用已存 `quota.observed_at/signals`；相对重置秒数从观察时间推算，标记 derived。零分钟窗口视为不存在。不把模型范围窗口与范围未知的响应头相加。

上游用量查询使用 CPAMP `/v0/management/api-call` 转发固定的 `GET https://chatgpt.com/backend-api/wham/usage`；每次先重新发现选中物理文件的 `auth_index` 与 workspace 身份，OAuth Token 由 CPA 替换，本机不读取。外层响应成功后仍检查上游 `status_code`。成功观察通过 `/v0/management/quota-snapshots` 写回，采用 partial inventory 保留未观察到的模型窗口，再重读规范化快照。主窗口按真实 duration 分类；代码审查、Spark 等 scope 独立保存。卡片优先展示主窗口，其他窗口保持可翻页访问。

`weather`：`status/fetchedAt/observedAt`、`city/temperature/low/high/code/isDay/message`，以及可选的 `hourly/timezone`。模型条件按 city 的本地日范围查询；后台 15 分钟，UI 30 分钟后或连接异常显示上次天气状态。

`hourly`：按绝对时间排序的小时数组，每项含 `time`（ISO 8601）、`temperature`（°C）、`precipitationProbability`（0–100%）、`precipitationMm`（该小时预计降水量，mm）、`code`（WMO 天气码）。除时间外字段可为 null；0 表示真实零值，不能替换为未知。概率限定 0–100，负降水量视为未知。桥接在原天气请求中查询城市本地两天数据，保留当前小时与未来 24 小时内的有效时段，最多 25 项；当天高低温仍取第一个本地日。

前端显示当前小时起的 12 个有效时段，按天气城市 `timezone` 格式化时间、标识跨日，缺少该字段时沿用配置时区。过期时段不继续展示；连接失败保留上次缓存并标识过期。旧桥接/缓存缺少 `hourly` 时保持可用，浮层显示小时预报暂不可用；契约仍为 `schemaVersion:1`。打开浮层不发起额外天气请求。

## 本机设置接口

全部 POST，在同源请求中以 `X-Wallpaper-Setup:1` 获取 `/v1/setup/session` 的两小时随机会话。其余接口要求 `X-Wallpaper-Admin` 会话并检查 Origin 和 Host。

- `config/read`：公开配置、是否已保存管理凭据、展示票据、有限诊断，以及 `quotaRefreshEnabled`、`quotaRefreshMinutes`、`quotaRefresh` 状态。
- `config`：按提交字段更新加密配置；管理凭据留空保留，`clearManagementKey` 清除；管理源变更清空之前账号绑定。
- `accounts`：读取账号列表，仅供本机设置使用；只有已发现的账号可绑定。
- `cities`：城市搜索；`sensors`：硬件传感器清单。
- `refresh`：重新读取缓存与天气，不查询 Provider 新额度。
- `quota-refresh`：对关注账号排队一次上游刷新；正在运行或已排队时合并请求。关闭自动刷新后仍可手动刷新。
- `quota-refresh/status`：只读取刷新状态，不触发远端请求。返回 `enabled`、`intervalMinutes`、`nextRefreshAt`、`state`；state 含 `lastAttemptAt/lastSuccessAt/lastFinishedAt/status/message/accounts`。逐账号结果仅含显示别名与固定说明。

刷新配置默认开启、10 分钟，间隔整数范围 1–10080 分钟，旧配置缺字段时使用默认值。下一次为最近尝试或完成时间加间隔；首次配置后立即获取。尝试开始前保存 DPAPI 状态，成功或失败完成后更新；重启中断的刷新保留节流时间。CPAMP 来源、凭据或账号集合变更重置该来源的调度，城市或别名变更保持原调度。来源变化时丢弃旧请求结果；停止自动刷新时停止后续账号请求。

本地文件 Origin `null` 与 loopback HTTP 来源可对展示接口做 CORS 预检，实际数据必须带票据。远端 Origin 拒绝；设置接口不向跨域页面开放。DNS rebinding 的非 loopback Host 被拒绝。管理页禁止嵌套 frame。

## 生命周期

硬件 2 秒，配额快照 60 秒，上游用量默认 10 分钟（可配置），天气 15 分钟。刷新任务仅由桥接调度，失败账号独立处理，不立即循环重试。前端每 2 秒读本地快照，失败指数退避至 30 秒，8 秒请求超时。壁纸暂停会停止前端轮询、时钟计时器、柔光更新；唤醒重新计算时间并连接。桥接独立运行，继续采集、刷新并缓存。硬件趋势 30 个实际样本；超过 10 秒的采样空档清空趋势，不补造数据。

所有后台远端请求有 15 秒超时、不携带凭据跨重定向；接口错误只返回筛选过的固定说明。管理凭据不写请求日志。当前本地 OS 用户或管理员仍可访问其自己的配置；loopback 与 DPAPI 不构成抵御本机已获授权恶意程序的隔离边界。
