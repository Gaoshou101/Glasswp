# 时间与天气展开页视觉深化

日期：2026-11-10。构建：`20261110092908-4b2d91f7`。

## 设计与实现

保持展开页原有顶部锚点和高度，保留人物留白、开合动效、鼠标输入保护和键盘焦点恢复。视觉使用现有 Segoe UI 字体与紫灰液态玻璃，暖金提示时间位置，浅蓝呈现降水。

- 时间：左侧主时区大字时间、完整日期、UTC 偏移与 24 小时刻度；右侧各地时间、日期差和时间位置。暖色轨道仅表示当地 06:00–18:00 的时段，不声称真实日出日落。列表较长时使用原详情容器滚动。
- 天气：左侧实况图标、温度、日范围和新鲜度；右侧天气图标、温度趋势、降水概率与降水量。温度缺失时曲线断开，未知降水与 0% 明确区分。
- 无小时预报仍保留实况；高德显示来源能力限制；过期数据标记上次实况与待更新。减少透明度时提供实色背景，沿用已有减少动态效果逻辑。
- 使用现有数据契约，不添加天气请求或定时器。世界时钟沿用缓存的 Intl formatter，新增当地分钟值以定位时间轨道。

主要实现：`src/card-details.ts`、`src/clock.css`、`src/clock-format.ts`。

## 验证

- `npm run check`：33 项单元测试、TypeScript、生产构建通过。
- `tests/world_clock_check.py`：实时更新、日期/偏移、管理设置、键盘与桌面 mouseup 保护、24 时区滚动通过。
- `tests/liquid_glass_check.py`：详情交互、切换、预报缺失/恢复、4 种竖屏尺寸、减少动态效果等回归通过。
- `tests/visual_polish_check.py`：图标、时间格式、材质与动态效果回归通过。
- 新增 `tests/top_details_check.py` 并加入 `scripts/verify.ps1 -Browser`：540、800、1080、1600 宽度，主时区 12/24 小时与秒显示、温度曲线缺口、0/未知降水、过期实况、无小时预报、减少动态效果通过；无浏览器错误。
- `git diff --check` 通过。

浏览器测试使用匿名模拟快照与本地 dist，不读取用户天气凭据、不修改正式设置。测试依赖 `.tools/python` 内 CPython 3.12 二进制包，应使用 `C:\Users\Xu\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe`；PATH 默认 Python 3.11 会因 greenlet ABI 不符而失败。

预览：`artifacts/validation/top-details/clock-1600.png`、`weather-1600.png`、`weather-stale.png`、`weather-empty.png`。

本次完成代码与本地构建，未安装到正式 Wallpaper Engine 或受保护桥接目录。工作区原有未提交改动保留；浏览器检查不等同于实际桌面点击验收。

## 同日排版调整

按用户截图删除时间页底部的日期基准/系统时间说明、天气页底部的降水图例/时区说明。扩大主时区时间字号、世界时钟行间距与预报项间距，重新分配垂直留白。校准值保留在主时区悬停提示；天气过期状态仍由实况状态与预报摘要表达。新构建：20261110103316-8288b564。生产构建、世界时钟检查与展开页多尺寸检查通过，未安装桌面运行版。


## 桌面更新完成

2026-11-10 19:09（Asia/Shanghai），用户要求“更新”后，已通过标准安装脚本将 `20261110103316-8288b564` 应用至 `portrait-dashboard-workshop` 当前 BOE1600 / UID524 显示器。

安装清单及文件哈希通过；Wallpaper Engine 已上报同一构建，1600×2560、30 FPS、未暂停；三组材质仍为 83/26/68。项目属性与宿主保存的壁纸属性未变化。本次仅更新桌面前端，桥接运行版未改动；未额外执行物理桌面点击验收。

备份：`F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\dashboard-backups\previous-f8a66d0388044d04a93d51212b392a4a`。

验证记录：`artifacts/validation/top-details-desktop-install.json`。此安装结果取代上文“未安装桌面运行版”的状态。
