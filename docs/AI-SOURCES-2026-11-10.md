# AI 套餐来源与本机识别覆盖（2026-11-10）

管理页提供三个互斥来源：CPAMP、本机自动识别、Magpie。选择本机来源后，桥接器并行检测已安装客户端的登录文件和只读额度接口；无需安装或启动 Magpie。选择 Magpie 则读取其缓存接口，适合已经在 Magpie 登录的其他供应商。此次修改仍在工作树，是否部署以最终交付记录为准。

## 使用和刷新

| 来源 | 条件 | 读取 |
| --- | --- | --- |
| CPAMP | 现有管理地址、管理密钥和关注账号 | 延续 CPAMP 账号选择及刷新设置 |
| 本机自动识别 | 当前 Windows 用户在对应原客户端已登录 | 检测下表客户端并读套餐、额度或钱包；找不到客户端时不造空账号 |
| Magpie | 网关默认 `http://127.0.0.1:3425`；启用鉴权时需网关 key | `GET /v1/magpie/quotas?kept=1`，解析 `{ "object": "list", "data": [...] }` |

新来源必须在设置中主动选择并保存；升级旧配置默认保持 CPAMP。切换来源保留各来源地址、密钥和账号设置，只有当前来源参与查询和展示。本机每 10 分钟自动读取，Magpie 每 60 秒读取缓存，手动刷新最短间隔 60 秒。Magpie 远程地址要求 HTTPS，本机回环允许 HTTP，key 不跟随地址变更。`kept=1` 读取 Magpie 已保存观测；缓存为空或过期需在 Magpie 处理。手动刷新不绕过原生接口冷却。

## 已实现的 Windows 原生读取

| 客户端 | 本地登录位置/方式 | 可读内容与边界 |
| --- | --- | --- |
| Codex | `$CODEX_HOME/auth.json`，默认 `~/.codex` | ChatGPT OAuth 套餐与 usage；仅 file 存储。API key、keyring、auto、ephemeral 不借用旧 auth.json |
| Claude Code | `$CLAUDE_CONFIG_DIR/.credentials.json`，默认 `~/.claude` | Claude OAuth 订阅及 5 小时/周等窗口；过期回原客户端登录 |
| GitHub Copilot | 编辑器 apps.json/hosts.json、Copilot CLI 本地凭据 | 已验证账号的套餐与高级请求/积分额度；仅官方 GitHub 主机 |
| Gemini CLI | `~/.gemini/oauth_creds.json` 与原客户端设置 | Google Code Assist 套餐、模型额度；API key 模式排除 OAuth 残留，不做 onboarding |
| Antigravity / agy | 已安装 `agy` CLI 的专用 usage 命令 | 使用固定无工具查询流程，输出只有额度证据时不发明账号或套餐；版本不满足合同则不运行 |
| Cursor CLI | `%APPDATA%/Cursor/auth.json`，`~/.cursor/cli-config.json` | 套餐与 planUsage；团队配置须匹配 authId。不实现 Cursor IDE SQLite 登录；缺额度不造百分比 |
| WorkBuddy / WorkBuddy AI | 桌面端 `desktop.info` 明文会话 | CN/国际版套餐与积分；`$wbEncrypted` 新格式明确提示当前不支持 |
| Trae CN / TRAE SOLO CN | `%APPDATA%` 内客户端 `User/globalStorage/storage.json` | 读取本机 token/userId，兼容 JSON 与上游两版 AES 编码，查询 CN 官方额度；不含 Trae Global |
| Kiro CLI / IDE | `%APPDATA%/kiro-cli/data.sqlite3` 的 auth_kv；`~/.aws/sso/cache/kiro-auth-token.json` | 套餐、各区域月度/试用/奖励/超额 credits；不刷新或写回 |
| ZCode | `~/.zcode/v2/credentials.json`、setting.json、config.json；支持 dataBaseDir | AES-GCM 解密，个人/已有团队项目密钥及 Start Plan；只读现有密钥，不创建 key、不领取活动 |
| Command Code | `~/.commandcode/auth.json` | 套餐和额度，购买金额与套餐额度分开 |
| Devin CLI | `%APPDATA%/devin/credentials.toml` | 额度与重置；只读原生 token |
| Grok Build | `$GROK_HOME/auth.json`，默认 `~/.grok` | usage；缺少已验证套餐名时不推断订阅等级 |
| CatPaw（美团妙手） | `$CATPAW_AUTH_PATH` 或 `~/.meituan-catpaw/auth.json` | 当前套餐与钱包余额 |
| 商汤小浣熊 Raccoon | 原生 agent config/auth.json；兼容旧 box-agent 布局 | 原生 bearer 账号余额 |
| AtomCode | `~/.atomcode/auth.toml` | CodingPlan 状态与额度；不执行领取套餐 |
| Warp | `%LOCALAPPDATA%/warp/Warp/data/dev.warp.Warp-User`（当前用户 DPAPI） | 官方 GraphQL request 额度；不发明付费等级 |
| Agnes Code | `%APPDATA%/AgnesCode` 的 Local State 与 code-auth-session.v1 | 当前用户 DPAPI + AES-GCM；credits balance |
| AStudio | `HKCU/Software/AStudioStorage` 注册数据目录及 `~/.acode/acode` | 桌面会话套餐与积分 |
| Kimi Work | `%APPDATA%/kimi-desktop/daimon-storage.json` 指向 share，或 daimon-share/daimon/config.json | 当前桌面账号会员月度共享额度；Code 是同一池占比，不拆成第二份额度；不含单独 Kimi Code API key |
| Codar（科搭） | `~/.codar/config.json` | 通用与 agent 余额分开 |
| DeepAI Code | 原生凭据自动探测 | Theos 额度和订阅摘要；上游只读 Theos GET 可能附带首次当日发放效果，详见下节 |
| TeleAgent | `~/.local/share/TeleAgent/users/<scope>` 和同 scope Chromium Cookies | 各账号积分 cookie，支持 Windows DPAPI/AES-GCM 和 v24 域名哈希；积分余额及套餐。不借用其他账号主分区 cookie |
| AutoClaw CN / 国际版 | `%APPDATA%/AutoClaw-official` / AutoClaw-oversea-official，Local State + accounts/*/account-credentials.enc | 当前用户 DPAPI/AES-GCM，会话钱包与可见积分池；不轮换 refresh token，避免使桌面登录失效 |

本机账号 ID 使用来源前缀和哈希。余额使用数量，未知额度保持未知；0 是真实耗尽或零余额，不是缺失。不限量不伪造百分比。登录在查询中切换时丢弃旧账号响应。

## 完整 registry 核验与不适用项

核验 Magpie 核心及官方社区 registry 全部 **49 个有 provider 声明的插件包、69 个 provider ID**，并读取七个外部维护包的发布源码。下表逐个包保留边界；“Magpie”表示可通过其独立登录后的额度缓存展示，不代表本机无需登录即可直接发现。
| 插件 / provider IDs | 核验结论 |
| --- | --- |
| Agnes AI / `agnes` | 独立 Chrome 浏览器登录；非原生 agent 自动登录，使用 Magpie |
| AtomCode / `atomcode` | 本机已实现 |
| GitHub Copilot / `github-copilot` | 本机已实现（核心编辑器/CLI 读取） |
| JoyCode / `joycode` | 京东浏览器 OAuth；使用 Magpie |
| 豆包工作 / `doubao-work` | 需启动独立桌面窗口登录并操作云任务；无套餐额度接口，不计入只读自动识别 |
| Kimi Work / `kimi-work` | 本机已实现 |
| OpenCode Zen Free / `opencode-zen-free` | 公开免费凭据；没有用户订阅额度 |
| ZCode / `zcode` | 本机已实现 |
| Qoder / `qoder, qoder-cn, qwenwork, qwenwork-enterprise` | 普通版设备 OAuth/PAT；企业 Lingma 本地缓存可登录但上游明确没有已知 usage 接口（index.mjs:3354）；不制造额度，其他登录使用 Magpie |
| Devin / `devin` | 本机已实现 |
| Antigravity / `antigravity` | 此插件浏览器 OAuth/import；本机 Antigravity 由 agy-cli 读取 |
| Zed / `zed` | 浏览器 OAuth；使用 Magpie |
| Factory / `factory` | 设备 OAuth/Factory API key；插件只读 client-phrases 静态文件，不是本地凭据，使用 Magpie |
| Grok / `grok` | 本机已实现 |
| Command Code / `commandcode-plan` | 本机已实现 |
| Xiaomi MiMo / `mimo-app` | 小米浏览器登录；使用 Magpie |
| Cursor / `cursor` | 本机 CLI 已实现；IDE SQLite 未实现 |
| Kiro / `kiro` | 本机已实现 |
| MiniMax Code / `minimax-code, minimax-code-global` | 设备登录；fs 只读 Magpie plugin-auth 工具配置，不是原生登录，使用 Magpie |
| Warp / `warp` | 本机已实现 |
| WorkBuddy / `workbuddy, workbuddy-ai` | 本机明文 desktop.info 已实现；新加密格式未支持 |
| Cline / `cline` | 浏览器 OAuth/API key；使用 Magpie |
| Trae / `trae-cn, trae-global` | 本机 CN/SOLO CN 已实现；Global 为浏览器登录，使用 Magpie |
| Open Design / `opendesign` | 第三方 API key；无原生自动登录 |
| CatPaw / `catpaw` | 本机已实现 |
| Loomy / `loomy` | 浏览器/手工凭据；使用 Magpie |
| Nous Portal (OAuth) / `nous-portal` | 设备码 OAuth；使用 Magpie |
| 商汤小浣熊 / `raccoon` | 本机已实现 |
| SenseNova / `sensenova-token-plan` | 网页/API 订阅凭据；使用 Magpie |
| 书生·端砚 / `intern-inkstone` | 网页/API 订阅凭据；使用 Magpie |
| AutoClaw / `autoclaw, autoclaw-cn` | 社区包仅网页/SMS；原生本机读取由 Providers Hub 路径实现 |
| CodeArts / OfficeAce / `codearts, officeace` | 华为浏览器 OAuth/ST​S；本地文件为签到账本，不是原生 agent 登录，使用 Magpie |
| GCP AI / `gcp-ai` | Google Cloud 控制台授权；文件只读 icon，非原生 agent 登录 |
| Cloudflare Workers AI / `cloudflare-workers-ai` | Cloudflare API key/账户配置；非原生 agent 套餐 |
| River / `river` | 账号/API 凭据；非原生自动登录，使用 Magpie |
| SAP AI Core / `sap-aicore` | SAP service key；云基础设施认证，非原生 agent 套餐 |
| OrcaTerm / `orcaterm` | 浏览器 OAuth；使用 Magpie |
| AWS Bedrock SSO / `bedrock-sso` | 读取 AWS profile/SSO，但 usage 是本地估算预算及可选付费 Cost Explorer；非 agent 订阅额度，未启用其付费查询 |
| Providers Hub / `hub-astudio, hub-accio, hub-lobsterai, hub-magicrew, hub-magicrew-intl, hub-autoclaw, hub-autoclaw-cn, hub-loomy, hub-monkeycode, hub-monkeycode-intl, hub-qiniu` | AutoClaw CN/global 原生读取已实现；AStudio 由独立原生读取器覆盖；其余渠道浏览器/API key/桥接登录，无新增原生 quota |
| AStudio / `astudio` | 本机已实现 |
| Agnes Code / `agnes-code` | 本机已实现 |
| Tabbit / `tabbit` | 上游本地读取仅 macOS；本项目为 Windows 桥接，未实现 macOS cookie/keychain 路径 |
| Codar (科搭) / `codar` | 本机已实现 |
| MClaw / `mclaw` | 浏览器/手工 API 凭据；使用 Magpie |
| DeepAI Code / `deepaicode` | 本机已实现 |
| Antigravity (local agy CLI) / `agy-cli` | 本机已实现 |
| TeleAgent 模型 / `teleagent` | 本机已实现 |
| 讯飞星辰 MaaS / `xfyun-maas, xfyun-coding, xfyun-token` | 讯飞 MaaS/Coding/Token API key；非原生自动登录 |
| Muse (Meta) / `muse` | Meta 设备 OAuth，并换 API key；无原生本地读取，使用 Magpie |

## 查询副作用与数据保护

本机模式只复用当前用户原客户端已保存的有效登录。查询期间读入的 token/key/cookie 不复制到应用配置，不返回浏览器，不发送给 CPAMP 或 Magpie。各适配器固定厂商 HTTPS 地址、禁用 HTTP 自动重定向、有超时和响应体上限。Kiro 的 List-Available-Profiles、Kimi 的 GetSubscriptionStats 等虽使用 POST，但操作是只读查询，不是模型推理。

桥接不主动调用 token 续期、不写回原客户端、不创建远程密钥、不执行单独的每日签到/领取、不运行完整上游插件。Antigravity 使用已安装 CLI 的专用 `/usage`，登录生命周期由原 CLI 管理。ZCode 仅在原客户端已记录已有项目密钥位置时查询现有 secret；团队密钥不存在时不会创建。AutoClaw 的上游插件导入流程会轮换 refresh token 并可能使桌面会话失效，本项目省略该流程；资产服务拒绝旧会话时请回原客户端处理。

DeepAI 的上游 `GET /deepai_code/theos` 本身具有“首次查询可能触发服务端当日额度发放”的语义。桥接未调用签到接口，但无法把厂商这个 GET 改成无此效果的纯读；适配器按账号至少间隔 10 分钟，403 后冷却 6 小时，手动刷新不绕过。若不接受该厂商行为，使用 CPAMP 或 Magpie 的已保存缓存来源。

本机失败会清除该次不可确认的窗口，其他已读成功的本机账号保留。Magpie 保留实际 `readAt/asOf` 与 stale 标记，读取缓存时不会把当前时间冒充厂商观测时间。新来源请求活动不借用 CPAMP 历史；不按邮箱跨来源合并。

## 源码和许可证

| 参考来源 | 固定版本 | 对应范围 |
| --- | --- | --- |
| [yetone/magpie](https://github.com/yetone/magpie) | `87b3af7653a762868a8d2ac5e3c9e6be5a1ff349` | 核心本地登录语义与 Quotas API |
| [magpie-community/plugins](https://github.com/magpie-community/plugins) | `8f4a8ec49680f83c4dee157edd07e270b0475b2b` | registry 49 包、69 provider；主要本机协议 |
| [magpie-providers-hub](https://github.com/Gyl1219/magpie-providers-hub) | npm `0.12.3` | AutoClaw CN/global 本机加密凭据与钱包 |
| [magpie-plugin-codar](https://github.com/MisonL/magpie-plugin-codar) | npm `0.1.2` | Codar |
| [magpie-deepaicode](https://github.com/aqiu817/magpie-deepaicode) | npm `0.2.3` | DeepAI Code |
| [opencode-teleagent-auth](https://github.com/anbulang/opencode-teleagent-auth) | npm `0.2.0` | TeleAgent |
| [magpie-muse-auth](https://github.com/sonvivu/magpie-muse-auth) | npm `0.1.10` | 核验无本机读取；未移植 |
| [opencode-xfyun-maas-auth](https://github.com/linjicong/opencode-xfyun-maas-auth) | npm `0.1.1` | 核验 API key；未移植 |
| [magpie-aws-bedrock-sso](https://github.com/HaroldLeo/magpie-aws-bedrock-sso) | npm `0.4.0` | 核验估算预算/可选付费账单；未移植 |

完整 MIT 版权与许可原文见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)，随桥接发布包带入。参考代码只用于协议移植；未安装运行这些插件或它们的推理/自动领取流程。

核心 `codex_store.go` 也明确未读取 Windows encrypted secret auth storage；不能把检测到设置名当成支持解密。Windows 桥接与 macOS 上游能力不同，Tabbit/macOS Keychain 等未声称支持。Qoder Lingma 的本地 cache 是模型访问凭据，上游 `qoder/index.mjs:3354` 明确无已知额度接口，因此不添加只能返回空卡的伪适配器。

## 验证范围

桥接模拟测试覆盖固定 API 目标、原凭据字节不变、身份切换丢弃、账号隔离、token/error 不外泄、DPAPI/AES-GCM、SQLite 只读、0/未知/不限/余额、试用/奖励/超额独立池、失败降级与冷却。最终集中验证通过 **1050 条桥接检查**、**35 条前端测试**与 TypeScript/Vite 构建。原有 Windows 原生窗口销毁事件测试曾出现时序失败，停止并行测试后独立完整复跑通过，未为绕过此失败更改该测试或输入保护实现。

多数原生厂商客户端未安装或未登录，协议来源和隔离 fixture 通过不等于所有厂商真实账号端到端通过；不虚报真实接入验证。此前实际 Codex 只读查询的结果仅代表当时观测。真实登录验证可在用户安装并登录对应客户端后通过本机来源完成。

检查入口：`npm run check`、`dotnet run --project bridge/WallpaperBridge.Tests`、`tests/ai_sources_admin_check.py`、`tests/ai_sources_api_check.py <dotnet路径>`。专用原生 fixture 位于 `bridge/WallpaperBridge.Tests/Local*QuotaChecks.cs` 和 `LocalDesktopWalletChecks.cs`。

## 候选交付（2026-11-11，Asia/Shanghai）

- 构建 `20261110155514-1451623c`（ID 时间为 UTC），位于 `artifacts/candidates/20261110155514-1451623c`，已同步到开发候选目录 `artifacts/staging`。
- 自包含 win-x64 发布成功；完整性清单、MIT notice、SQLite 原生 DLL 均验证存在。没有安装到 `E:\WallpaperDashboard\current`，没有提交或推送。
- AI 管理页 7 组、既有管理页 15 组、时钟天气管理页 16 组通过；来源 API 鉴权/单选切换/密钥保留/冷却/旧结果隔离通过。
- 新增 `tests/ai_quota_wallet_check.py` 验证实际金额、零钱包、不限量、隐藏无意义百分比条及 800/1600 宽布局；额度分页、多尺寸与耗尽语义浏览器回归通过，无浏览器异常。
- 发布事务 12 条检查与 `tests/bridge-release-check.ps1` 隔离启动失败回退验证通过。隔离验证确认程序、加密配置、展示票据和硬件采样恢复，正式运行不受影响。
- 检查日志：`artifacts/validation/ai-all-bridge-checks.log`；钱包截图：`artifacts/validation/ai-wallet/`；发布回退报告：`artifacts/validation/bridge-release-rollback.json`。

2026-11-11 后续已获安装授权，最终构建 `20261110161348-d8a34615` 已安装并完成真实 Magpie 接入验证，覆盖并取代上述“尚未安装”的阶段状态。Codex 读取成功，WorkBuddy 上游 HTTP 401；最终来源为 Magpie。详见 [安装实测记录](MAGPIE-INSTALL-2026-11-11.md)。
