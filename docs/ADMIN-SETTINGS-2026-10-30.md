# 本机设置优化与验收（2026-10-30）

## 后续更新：Antigravity 真实套餐

同日用户补充确认另外 4 个账号为 Google AI Pro，并要求优先查询上游，失败时显示 `antigravity`。以下更新取代初次验收中「4 个空套餐」的状态：

- 通过已有 CPAMP `/v0/management/api-call` 代理只读查询 `loadCodeAssist`，4/4 HTTP 200，均返回 `paidTier.id=g1-pro-tier`、`paidTier.name=Google AI Pro`。`currentTier` 的 free/standard 仅代表基础层，不能覆盖付费层；`allowedTiers` 不作为已订阅证据。
- 新增 `AntigravitySubscriptionReader`，优先 paidTier，支持 camel/snake case 字段；为共享额度快照补充套餐名，因此设置页和桌面卡片同时受益。
- 成功缓存 1 小时，失败 10 分钟后重试，已知套餐最长保留 24 小时；凭据/源地址或当前账号身份变化不复用旧缓存。查询总预算 12 秒，401/403/429 不连续尝试后备端点，应用停止正常传播取消。
- 无可用套餐时使用 `antigravity`，前端对旧桥接的空套餐也兼容此回退。套餐查询失败不改变正常额度窗口与新鲜度。
- 验证：270 项桥接检查、25 项前端测试、8 组浏览器回归、发布回退测试及隔离端口候选启动，退出码均为 0。实机 10/10 套餐标签与快照一致，4/4 Antigravity 为 Google AI Pro，配置写入 0、浏览器错误 0。
- 已用原更新脚本安装桥接 `20261030144349-fb9d986f`，保留管理员计划任务、原私有配置与已安装壁纸静态资源。旧发布备份位于原运行目录的 `artifacts/backups/bridge-releases/previous-e2f6e5d103b6477a8b7aab0ac4e001c9`。本次重启桥接，没有迁移运行目录或发布工坊。

新增证据：`artifacts/validation/antigravity-subscription-probe.json`（仅套餐字段与匿名序号）、`antigravity-subscription-bridge-tests.txt`、`admin-settings-result.json`、`admin-settings-live-result.json`。凭据仅在请求进程内读取使用，没有记录到测试输出或源文件。

下文保留首次页面优化的历史记录。

## 已完成

- 设置页、浏览器预览页加入紫色竖屏壁纸 SVG 图标。
- 卡片排序按账号 ID 匹配最近本机快照的套餐名，显示在别名旁；不额外向上游请求用量。
- 每 15 秒读取本机快照，后台页面暂停轮询；8 秒超时后可自动重试。只更新套餐标签，保留未保存顺序和键盘焦点。
- 套餐缺失显示「套餐待同步」；断连时保留本页已知套餐，并在悬浮提示中说明，首次读取失败显示「套餐暂不可用」。
- 增加账号总数和「撤销排序」，恢复已保存的顺序后取消待保存状态。保存失败仍保留草稿。
- 长别名和套餐支持省略提示，窄屏自动换行；刷新说明补充 Antigravity。

## 验证

- `npm.cmd run check`：25/25 前端测试通过，TypeScript 与 Vite 构建成功，退出码 0。
- 最终 `npm.cmd run build`：退出码 0。
- `tests/admin_settings_check.py`：7 组浏览器回归通过，退出码 0。覆盖 ID 对应、草稿/焦点、撤销、保存失败与重开、快照错误恢复、360/540/900/1440 宽度、文本安全及图标。API 全部使用匿名测试数据。
- 运行中的 `http://127.0.0.1:17632/admin/`：10 个账号顺序与保存配置一致，6 个套餐标签与快照一致，4 个空套餐显示待同步；图标、排序草稿撤销和 360/1080 宽度检查通过，浏览器错误 0，配置写入 0。
- `node --check public/admin/admin.js`、针对修改文件的 `git diff --check`：退出码 0。

截图和 JSON 证据位于 `artifacts/validation/admin-settings-*`。

## 本机应用

代码工作区为 `E:\Codex\repositories\wallpaper`。当前计划任务仍指向旧工作区 `C:\Users\Xu\OneDrive\文档\ChatGPT\wallpaper`，运行时网页根目录为其 `artifacts\bridge\wallpaper`；已通过计划任务、健康构建 ID 和 HTTP 文件内容匹配确认。

仅应用管理页 HTML/JS、favicon，以及预览 HTML 的图标引用，同时更新两个构建清单的文件哈希并添加独立的 `settingsWebPatch` 记录。桥接二进制构建 ID 保持原值；没有重启桥接或写入用户配置。发布前逐文件验证旧清单，更新后逐文件和 HTTP 回读验证。

备份：`artifacts/backups/settings-web/20261030T140316Z`；应用记录：`artifacts/validation/settings-web-deployment.json`。较早一次应用因记录构造出错已自动还原，修正后重新完整验证并成功应用。

没有迁移旧工作区或自启动入口，没有替换桌面壁纸包，也没有发布创意工坊。

## 后续建议（待选择）

1. 把关注选择、别名、套餐和排序集中到一个账号列表，支持搜索与平台筛选；10 个账号时比跨区操作更方便。
2. 为账号编辑、刷新间隔等增加未保存标记和离页提醒，避免误刷新丢失草稿。
3. 把传感器原始报告与连接诊断放进默认折叠的「高级诊断」，日常设置页只保留可读状态。
4. 单独处理旧目录自启动入口到 E 盘工作区的迁移，保留现有私有数据位置并验证回退；本次尚未进行迁移。
