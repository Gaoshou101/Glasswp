# 绘图日额度误归类修复

状态：服务端补丁已撤回；原官方 v1.14.7 与原定制面板已恢复，改用本机桥接修复。2026-10-19，Asia/Shanghai。

## 当前方案：本地修复，服务端恢复原版

用户担心后续 CPA 升级兼容性，要求停止维护服务端补丁。23:43:23 已恢复原程序与原面板，23:44:45 完成数据保护验证。以下“服务端补丁历史”仅供追溯，不能作为当前部署状态。

| 恢复检查 | 结果 |
| --- | --- |
| 版本 | 官方 `v1.14.7` |
| 原程序 SHA256 | `f899b76c597b308a3d8bdb97482f5d3d30aa7cd6566560016134a0e3cbf950a4` |
| 原定制面板 SHA256 | `2f8adf720ff9d13b26b44c041ab1e2743a99c9011e7950628d42600fda916a45`，实际 HTTP 面板一致 |
| 服务 | `active`，`/health` 为 `ok:true` |
| 数据保护 | 未恢复旧数据库；schema 不变，历史请求 97,603 → 97,616，两次 SQLite quick_check 均为 ok |
| 配置、service unit、secrets | 恢复前后哈希一致 |
| 回退材料 | 原始备份和已撤回补丁副本均保留于远端私有目录 |

本地 `LocalQuotaClassifier.cs` 只针对原版 CPAMP 的 Codex 响应头日窗口，在查询结果副本中纠正范围和窗口 ID；不写入服务端。通过现有 monitoring analytics 接口按账号、精确毫秒时间查询，核对请求 ID/事件 hash、窗口时长与用量，再读取 `active_limit=imagegen*`。只有 active limit 缺失时才使用优先级最高的模型名判断。

余额和重置时间整体保留。每字段的额度观察时间优先于窗口整体的最新边界时间。真正的普通日额度保持原样；同一绘图 ID 的新旧记录保留最新完整观察，避免撤回补丁后旧 feature 范围与新旧版记录重复展示。已有响应头缓存使用相同绘图名称，合并时不会再增加重复窗口。

该分类器不扫描普通 5 小时和周额度；每账号每轮最多查询 8 个日额度观察、总超时 4 秒、缓存最多 128 项。接口缺失、超时、证据歧义或不匹配时保留原额度，不阻断常规快照读取。未来接口变更可能使本地分类退回原始名称，需要届时适配；无需重打服务端程序或面板补丁。

验证：修复前回归用例失败；修复后 **153 项桥接检查通过**，包含恢复后的原版服务端三个历史绘图请求的只读回放、账号隔离、普通日额度、错误降级、来源字段时间、重复窗口、配置加密与刷新调度。壁纸前端 **16 项通过**；正式 .NET 发布成功。安装核对展示票据、账号顺序、城市、传感器、连接和刷新设置。

本地证据：Git 忽略的 `artifacts/imagegen-fix/restore-verification.json`、`local-live-replay.json`、`local-bridge-tests.log`、`local-bridge-install.json`、`local-final-live-check.json`。

23:52:18 本地桥接安装成功，所有配置保留检查通过；运行 DLL SHA256 为 `fae2f2955cdd653364baf2c6d779eec48319448dc687a6c2d113adcfba6fda53`。23:53 实时读取三个账号含 5 个普通窗口及 1 个明确标注的绘图日窗口；该绘图历史观察已超过 15 分钟，正确标记过期，普通窗口均未过期。此前常规上游刷新 3/3 成功，10 分钟自动刷新保留；Wallpaper Engine 客户端在线。绘图额度保留后可能增加一页，这是保留真实额度的预期行为。

### 2026-10-20：重复窗口分页修复

用户截图补充确认另有重复窗口：第一页为三个账号的普通额度，第二页出现绘图额度时，后两个账号再次展示第一页的普通额度。实时桥接窗口 ID 无重复，原因在本地 `src/quota.ts`：账号没有第 N 页切片时，原规则回退到第一个切片；原测试甚至断言应重复展示该切片。

现改为每个窗口只出现在自己的切片。第一页保留全部账号（含空额度与被过滤后无窗口的状态）；后续页只包含仍有未展示窗口的账号。真实 3/2/1 场景中，第一页三个账号、五个普通窗口；第二页仅第一个账号的绘图日额度。保留绘图额度、过期提示、别名、账号顺序及全部其他窗口。

修复前两个回归用例失败；修复后 17 项前端测试、TypeScript/Vite 正式构建通过。新增不均匀窗口数、超过三个账号、空账号、单账号轮播和每个账号窗口恰好出现一次的检查。安装后真实生产 JS 浏览器验证：两页卡片数 3/1，无重复，往返翻页和刷新保持当前页通过，无浏览器错误；既有 12 种竖屏尺寸、过期/未知/零值/断连/恢复回归通过。

11:08:33 更新本机当前壁纸、其他已有本地副本及桥接静态包，仅替换 `wallpaper.js`；未改服务端或重新上传 Steam。展示器选中壁纸、全部属性覆盖与各 `project.json` 哈希保持一致。只重载当前仪表盘所在显示器，1600×2560、30 FPS 宿主重新上报正常。备份为 `artifacts/backups/quota-pagination-20261020-110829`；安装结果为 `artifacts/validation/quota-pagination-install.json`，截图与浏览器结果位于 `artifacts/validation/quota-pagination-20261020/`。

### 2026-10-20：绘图额度独立显示开关

按用户要求，在 Wallpaper Engine 属性和浏览器外观设置中新增「忽略 Codex 绘图额度（imagegen）」。默认未勾选，保留绘图额度；勾选隐藏、取消恢复，与 luna-release 的基础模型周窗口开关独立。过滤发生在分页与过期提示计算前，支持多账号卡片和单账号轮播；普通日额度与其他平台窗口仍显示。账号全部窗口被过滤时保留账号，明确提示「绘图额度已忽略」或「基础模型周窗口和绘图额度已忽略」。不修改额度记录。

11:29:45 已安装到当前本机桌面、其他既有本地壁纸副本和桥接静态包，仅更新 HTML/JS 并增加 `ignore_codex_image_quota` 属性，默认值为 `false`。安装核对构建资源一致，所有原项目字段、展示票据、显示器选中壁纸和属性覆盖保留；只重载当前仪表盘显示器，1600×2560、30 FPS 宿主上报正常。备份位于 `artifacts/backups/image-quota-toggle-20261020-112941`，安装报告为 `artifacts/validation/image-quota-toggle-install.json`。

验证：18 项前端测试、TypeScript/Vite 正式构建通过；安装后的生产资源通过浏览器检查，包含旧设置默认显示、隐藏后移除额外分页与过期提示、设置重载后保留、绘图与 luna-release 两开关独立、WE 属性隐藏与恢复、仅含绘图额度的账号保留、单账号轮播过滤。既有 luna-release 检查全部通过，无浏览器错误。

实时账号窗口数已从早前的 3/2/1 变为 3/3/2，因此分页浏览器检查改为冻结实时快照后按窗口数量计算预期页面，避免把正常额度变化误报为缺陷。验证两页卡片数为 3/2，每个账号窗口只显示一次，往返翻页与刷新保留当前页均通过，无浏览器错误。绘图过滤的确定性 3/2/1 场景仍由单元测试和专用浏览器检查覆盖。报告位于 `artifacts/validation/image-quota-ignore-result.json`、`artifacts/validation/luna-ignore-result.json` 与 `artifacts/validation/quota-pagination-20261020/result.json`。

本次未修改服务端、重新发布桥接程序或上传 Steam 创意工坊。

## 服务端补丁历史（已撤回）

## 原因与处理结果

`gpt-image-2.5-sunburst` 返回的 `active_limit=imagegen_premium`、1440 分钟额度被 CPAMP 当作 `family/codex_main`，写成 `window-1d-0`，从而给壁纸增加了不明 `daily` 窗口和页面。该真实绘图额度现在独立保存和显示；没有按窗口时长隐藏数据。

线上安装目录仍叫 `cpa-manager-plus_v1.13.1_linux_amd64`，但其原程序实际为官方 **v1.14.7**。通过原二进制的 Go 构建信息与官方标签同时确认提交为 `64ce94e6aa1af691555429a3211e67c59c0480ad`。本次以该版本源码为基线，未跨版本升级应用。

## 历史服务端补丁内容（已撤回）

1. 后端响应头入库优先识别 `imagegen` / `imagegen_*`。缺少 active limit 时，按 resolved model 优先顺序识别 `gpt-image-*` / `dall-e-*`；明确的非绘图 limit 不被模型名覆盖。
2. 绘图使用 `feature/imagegen_premium`、`imagegen-premium-0-window-1d-0` 及独立 `codex:feature:imagegen_premium` inventory。主额度完整清单不会把绘图额度标为缺失或失效。
3. 前后端使用同一窗口 ID。绘图服务器观察使用 canonical event hash；仍打开的旧面板提交主范围时，后端可根据同一 event hash、时间、时长、用量的已保存绘图证据纠正归类。该防护针对补丁上线后产生的新绘图证据。
4. 定制管理面板保留原文件，仅替换一处额度范围表达式；脚本会验证原文件 SHA256，并检查可逆替换后与原文件完全相同。普通 Codex 与 Spark 的范围保持原语义。
5. 壁纸和缓存响应头把绘图额度标为 **“日窗口 · 绘图（Premium）”**，保留零用量、重置时间和真实余额；绘图缓存的 ID 与主额度独立。同为一天的绘图额度和主额度可以同时存在。

真实绘图额度再次被观察到时，仍可能增加页面，这是用户选择保留真实额度后的正常行为。已失效的历史误归类记录保留，本次未重写历史数据。

## 验证

| 项目 | 结果 |
| --- | --- |
| 修复前复现 | 同一历史响应头模型与额度数据，使前后端新增归类测试各 4 个用例失败；桥接新增名称测试失败 |
| CPAMP web 全部组件测试 | 261 个文件，4,424 项通过 |
| 最终相关前端测试 | 3 个文件，157 项通过 |
| 前端正式构建 | TypeScript 检查和 Vite 构建通过 |
| 最终 Linux 额度模块 | 179 项通过，含子用例；隔离临时 SQLite，不接入生产数据 |
| 定制面板定点补丁 | 7 个范围用例通过，仅额度表达式改变 |
| 本地桥接 | 127 项通过，正式发布成功 |
| 壁纸现有前端 | 16 项通过 |
| 线上正常额度刷新 | 22:36:07 完成，3/3 成功，2/2/1 个窗口，共 5 个 |
| 最终桥接安装 | 22:38:18 验证通过，票据、账号顺序、管理连接、刷新设置、传感器和城市保留 |
| 远端运行状态 | `active`，`/health` 返回 `ok:true`，HTTP 面板哈希与候选一致 |
| 数据保护核对 | 数据库 schema 未变；历史 usage 行数 97,261 → 97,281；SQLite quick_check 为 ok；配置、service unit、secrets 文件一致 |

未为验证而发起付费绘图请求。绘图场景用已捕获的模型、active limit、日窗口数据离线回放；线上验证使用正常的用量查询。

完整 Go 仓库测试也已执行，但 Windows 不支持的进程信号、Unix 文件权限及被占用文件清理导致部分失败。仓库发布/安装脚本测试也受 Bash、Git release 环境限制，未全部通过。本次受影响的额度模块已在 Linux 完整通过；上述结果不代表整个上游仓库的全部测试都通过。

## 交付与版本

- 上游源码补丁：[cpamp-v1.14.7-imagegen.patch](../patches/cpamp-v1.14.7-imagegen.patch)。使用 `git apply --check` 检查后应用到官方 v1.14.7。
- 定制面板脚本：[patch-cpamp-imagegen-panel.mjs](../scripts/patch-cpamp-imagegen-panel.mjs)。要求原面板精确匹配，拒绝其他构建。
- 本地源文件：`bridge/WallpaperBridge/QuotaAdapter.cs`、`CachedHeaderAdapter.cs` 及 `bridge/WallpaperBridge.Tests/Program.cs`。
- 已撤回版本：`v1.14.7-imagegen-fix.1`；构建工具 Go 1.27.2，Linux amd64、CGO 禁用。
- 已撤回二进制 SHA256：`dcd4c3daaa6c42a5f875fe37005a2f35bc243788de0b70f58290e280efb39a2e`。
- 已撤回面板 SHA256：`eeb0c4f671d52ea4e444f5a475da9672ecde4dfa2c01bcdf8e61059fda5569ab`。
- 诊断、构建、验证、部署脚本与日志位于 Git 忽略的 `artifacts/imagegen-fix/`，不含访问令牌和私钥。

## 回滚

远端备份：`/root/cpa-manager-plus/imagegen-fix-20261019/backup-20261019T143213Z`，含原程序、原面板、配置、secrets、停止服务后的数据库及存在的 sidecar 文件。

回滚应用时，停止 `cpa-manager-plus`，从上述目录恢复原程序和 `panel.html` 至其原路径，启动服务并检查 `/health`。应用修复没有改 schema，通常只恢复程序和面板；恢复旧数据库会丢失备份之后的新请求记录，应单独决定。

本地最终桥接备份：`artifacts/backups/imagegen-fix-20261019-223814`。停用本项目桥接任务，恢复该目录的发布文件至 `artifacts/bridge`，再启动 `WallpaperDashboardBridge` 任务。原始修复前版本另保存在 `artifacts/backups/imagegen-fix-20261019-223123`。

当前不再向后续官方版本重放该服务端补丁。以上补丁及脚本仅保留作诊断与历史追溯材料。
