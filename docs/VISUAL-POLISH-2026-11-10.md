# 壁纸视觉细化验收（2026-11-10）

本次接续「美化壁纸整体显示效果」对话中的未提交实现，完成代码复核、修复与生产构建验收。保留原图、竖屏布局、人物面部留白和材质设置，视觉规则集中在 `src/polish.css`。

## 改动

- 液态玻璃增加柔和明暗层次、边缘反光和随鼠标移动的局部高光，压低亮背景对文字的干扰；磨砂模式同步调整明暗。
- 统一系统字体栈、数字字重、日期间距、套餐标签、按钮与额度层级，无外部字体请求。
- 天气按晴天、晴夜、多云、雾、雨、雪、雷暴绘制 SVG；平台按后续要求改为官方高清图标、统一尺寸和等比显示，来源见 [图标记录](PROVIDER-ICONS.md)。
- 硬件加入分类图标、分离百分比单位、带填充的趋势曲线；缺样仍断开，不补造数值。
- 审查修复：普通进度条背景只作用于已知额度，保留未知额度斜纹；新边缘光在停用动态、暂停及减少动态效果时取消过渡；负值与非整数的低位天气码不再误画为多云。

## 验证

所有浏览器检查使用生产 `dist/index.html` 与匿名拦截数据，不调用真实上游或更改正式设置。

| 检查 | 结果 |
| --- | --- |
| `npm run check` | 29 项单元测试、TypeScript 检查和 Vite 打包通过 |
| `tests/liquid_glass_check.py` | 交互、详情面板、焦点、闲置停帧、暂停及减少动态效果通过；800×1280、1080×1920、1600×2560、2160×3840 无溢出 |
| `tests/quota_ui_check.py` | 四种竖屏尺寸、卡片与轮播、五项密集额度、分页、焦点和过期状态通过 |
| `tests/quota_exhausted_check.py` | 六项耗尽与重置场景通过 |
| `tests/visual_polish_check.py` | 未知斜纹、平台/硬件配色、天气切换、百分比文本、缺样曲线、16 组时钟布局、边缘光动态限制通过 |
| 人工截图复核 | 1600×2560、1080×1920、悬停、详情、轮播和磨砂模式；浏览器错误为 0 |

新增的视觉回归已接入 `scripts/verify.ps1 -Browser`。本轮未运行该入口中的桥接后端和正式安装测试；后端没有修改。

## 本地交付

- Wallpaper Engine 候选包：`dist/project.json`；构建标识及哈希：`dist/build-info.json`。
- 最终截图：`artifacts/tmp/polish/final-20261110/`；默认材质截图：`artifacts/tmp/polish/default-20261110/`。
- 后续官方图标版本截图：`artifacts/tmp/polish/official-icons-20261110/`；29 项单元测试、构建与视觉回归再次通过，图像加载及统一尺寸已检查。
- 视觉回归结果：`artifacts/validation/visual-polish/result.json`；液态回归：`artifacts/validation/liquid-glass-check.json`。
- 上述浏览器截图使用匿名示例数据，不代表实时账号用量。后续正式桌面安装见下节。

源码改动保留在当前 `codex/stability-efficiency` 工作区，构建记录如实标记 `dirty: true`。产物及截图按既有规则不进入 Git。

## 正式桌面安装

2026-11-10 用户明确要求「应用到桌面吧」后，已安装构建 `20261110035126-63ca372a`，包含官方平台图标及透明底 Antigravity 标志。

- 目标：`F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\myprojects\portrait-dashboard-workshop`；仅应用到当前活动的 BOE1600 / UID524 显示器。
- 通过 `scripts/install-wallpaper.ps1 -Apply -Location` 备份、更新并重新加载。
- 回退副本：`F:\SteamLibrary\steamapps\common\wallpaper_engine\projects\dashboard-backups\previous-583313dd913340ebb6cd554117641586`。
- 安装文件哈希全部通过；既有属性值保留。Wallpaper Engine 上报新构建、1600×2560、30 FPS、未暂停，三组材质仍为 83/26/68。
- 未更新桥接程序或上传 Steam 创意工坊。本次未额外执行真实桌面点击验证。
- 证据：`artifacts/validation/visual-polish-desktop-install.json`。
