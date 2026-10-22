# 壁纸额外额度窗口与 daily 排查

状态：DONE（诊断完成；未修改生产代码或配置）。日期：2026-10-19，Asia/Shanghai。

后续处理：曾部署服务端补丁；用户随后要求避免 CPA 升级兼容问题，已于 23:43 恢复原官方程序及原定制面板，并改用本机桥接修复，详见 [修复报告](DAILY-QUOTA-FIX-2026-10-19.md)。原线上程序实际为官方 v1.14.7，提交 `64ce94e6aa1af691555429a3211e67c59c0480ad`；目录名保留 v1.13.1。以下内容保留诊断阶段的证据和版本核对边界。

## 结论

`daily` 来自绘图服务的真实 24 小时额度响应，被 CPAMP 错归到 Codex 主套餐范围；壁纸按上游窗口原样展示并增加分页。不是本地重复启动账号或桥接进程。异常已在本次排查开始前自行恢复，用户也确认桌面恢复正常。

## 已验证证据

- 本机 `127.0.0.1:17632` 只有一个桥接监听实例，管理员模式；WE 的浏览器、GPU、网络、存储、渲染子进程属于一个宿主进程树。
- 三个账号历史均存在 `provider_window_id=window-1d-0`、`window_kind=daily`、`duration_seconds=86400`、`source=response_header`、`model_scope_key=codex_main`。
- 查询这些窗口首次和最后一次观察时间对应的请求，六条记录全部来自 `gpt-image-2.5-sunburst`，响应额度均为 `active_limit=imagegen_premium`、`primary.window_minutes=1440`。这确认它们属于绘图日额度。
- `bittt233` 首次记录时间为 13:52:09，`xuhyu97` 为 16:18:36，`hyxu777` 为 16:20:17；最后观察集中在 16:28–16:29。三个 daily 窗口的 `deactivated_at_ms` 均为 16:48:29 左右，当前全部 `inactive`。
- 21:28:37 复核：3 个账号、5 个有效窗口（2/2/1）、daily 为 0；自动刷新 3/3 成功，最近完成时间 21:28:36；WE 客户端仍在线。
- 已安装壁纸的 JS/CSS 与项目现有 dist 哈希一致，没有发现前端版本混用。

诊断结果保存在被 Git 忽略的 `artifacts/validation/daily-quota-investigation-20261019.json`；对应只读采集脚本为 `artifacts/validation/investigate-daily-quota.ps1`。均未保存访问凭据或令牌。

## 代码链路

核对的是 CPAMP 公开 v1.13.1 源码；现场服务的实际完整源码版本未重新认证，现场数据与以下路径一致。

1. [codexquota/scope.go](https://github.com/seakee/CPA-Manager-Plus/blob/v1.13.1/apps/manager-server/internal/codexquota/scope.go)：`ResolveUsageScope` → `usageScopeForModel` 仅单独识别 Spark，其余有名称的模型均返回 `MainScope()`；绘图模型因此也被划入 `codex_main`。
2. [quotasnapshot/evidence.go](https://github.com/seakee/CPA-Manager-Plus/blob/v1.13.1/apps/manager-server/internal/service/quotasnapshot/evidence.go)：`quotaSnapshotEntryFromUsageEvent` 取上述范围，未根据 `active_limit=imagegen_premium` 隔离绘图额度；1440 分钟按 duration 分类为 daily，ID 生成 `window-1d-0`，作为 partial inventory 写入。
3. 本地 `bridge/WallpaperBridge/QuotaAdapter.cs` 排除 inactive/removed/missing，其余窗口按 `window_kind` 显示。daily 没有专门的中文名称，所以显示英文 `daily`。
4. 本地 `src/quota.ts` 每账号每页最多展示两个窗口，窗口增多会增加页面数。恢复前的完整前端快照未保留，不能独立证明用户所见“六个窗口页面”的逐页组成；额外 daily 是已证实的增页来源。
5. 壁纸上游刷新使用 partial inventory，保留未观察到的范围；CPAMP 生命周期可通过后续完整观察或移除事件使旧窗口失效。现场仅确认了失效时间，未确认是哪次任务执行了失效操作。

## 处理边界与后续修复位置

本次仅执行查询和本地诊断材料保存，没有改动生产代码、账号顺序、票据、刷新设置或远端数据，也未手动触发 Provider 额度刷新。已有自动刷新继续正常运行。

要避免复发，应在 CPAMP 的响应头额度入库路径中，按 `active_limit` 与模型区分绘图、Spark、主套餐等额度范围，为绘图额度使用独立 ID 和名称，并防止 Codex 主套餐完整清单误使独立绘图额度失效。仅在壁纸隐藏所有 daily 会丢失真实额度，且无法修正 CPAMP 中的范围归类。当前自动恢复不代表该归类缺陷已修复。
