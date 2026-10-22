# Wallpaper Engine 创作者作品研究

研究日期：2026-10-10。目标：从真实作品学习内容设计、交互与制作路线，为本项目后续原型提供依据。前置资料见 [PC 文档与接口架构研究](WALLPAPER-ENGINE-RESEARCH.md)。本文尚未确定项目技术栈。

## 1. 范围与证据

完整浏览用户指定的两份公开作品目录：夜莺 Night 5 页、Hope 麻匪 7 页，共 **355 个不重复目录条目**。目录包含壁纸和素材，不能全部计作壁纸。选取 20 个条目读取详细说明，再沿作者引用补充 2 个素材来源，共 **22 个详情页**。

另使用 Chromium 查看 **8 个详情页的静态预览**，抽样播放 Dock2 所链接的 51 秒演示视频，查看约 10、26、42 秒画面。没有订阅、下载或运行 Workshop 项目，没有查看其源码，没有实测 GPU、内存、帧率或输入行为。其余演示视频仅记录入口，尚未观看。

本文证据标记：

- **页面事实**：标题、作者说明、标签或分类。功能标签是公开标注，不能替代运行验证。
- **预览观察**：实际看到的图片或演示画面。宣传视频不能证明所有机器上的实际行为。
- **实现候选**：依据官方接口提出的实现方式，不能当作作者源码结论。
- **项目建议**：从案例归纳出的开发建议，尚未成为已批准需求。

来源清单见 [SOURCES.md](../research/creators/SOURCES.md)，结构化说明见 [sources.json](../research/creators/sources.json)。原始 HTML、正文及浏览器截图本地留存且已加入 Git 忽略。抓取时出现 SSL 中断与 HTTP 429，已用缓存、有限重试与浏览器补齐，最终详情清单无失败条目。

## 2. 创作者与学习方向

| 创作者 | 公开目录规模 / 关注者快照 | 本次样本体现的方向 | 学习重点 |
| --- | --- | --- | --- |
| [夜莺 Night](https://steamcommunity.com/profiles/76561199211352973/myworkshopfiles/?appid=431960) | 147 条 / 41,651 关注者 | 角色、风景氛围，时间变化、视差、形变、可互动 | 原画如何转成分层场景；如何增加互动同时保留画面主题 |
| [Hope 麻匪](https://steamcommunity.com/profiles/76561198109148999/myworkshopfiles/?appid=431960) | 208 条 / 94,196 关注者 | 桌面组件、快捷启动、媒体信息、音频互动、Shader 和 Web 实验 | 将组件和环境效果组合；根据效果选择 Scene 或 Web |
| [seekay](https://steamcommunity.com/id/_seekay_/myworkshopfiles/?appid=431960) 与 [seekay_labs](https://steamcommunity.com/id/_seekay_labs_/myworkshopfiles/?appid=431960) | 本次只沿作品引用研究，未全量浏览目录 | Utopia、可复用 Dock 素材 | 将成熟功能沉淀为素材，建立可追溯的引用链 |
| [JPL](https://steamcommunity.com/profiles/76561198189897202/myworkshopfiles/?appid=431960) | 仅查看与麻匪共同署名的作品 | 漂 雲2 决定版 | 氛围画面与合作制作、原画及音乐署名 |

关注者数字会变化，仅用于确认用户指定作者具有公开受众，不构成创作者排名。seekay 与 seekay_labs 是两个页面账号；本次没有证据确认账号关系。JPL 未进行知名度排名核验。

## 3. 夜莺 Night：从原画组织场景

### 3.1 伊蕾娜：闲憩微息 / Day Night Gradient

[作品页](https://steamcommunity.com/sharedfiles/filedetails/?id=3470764447) · [页面所链接演示](https://www.youtube.com/watch?v=dxy1pQRu9Po)

**页面事实**：Scene，3840×2160，标注可自定义、音频响应、媒体集成、视频纹理；标题明确时间变化。原画署名あずーる / Azure。作者说明移动端导入黑屏时应关闭随时间变化功能。页面约 24,385 次评分，但没有将评分量当作订阅量。

**预览观察**：人物位于中右部，左侧暖色窗光与右侧冷色阴影形成对比；前景植物和部分背景虚化，脸部与杯子清晰。它通过明暗、冷暖和清晰度建立视觉层次。静态图不能证明日夜过渡曲线、动画速度或是否有真实景深计算。

**实现候选**：官方 [Time of Day 教程](https://docs.wallpaperengine.io/en/scene/scenescript/tutorial/timeofday.html)提供 `engine.timeOfDay` 与 `WEMath.smoothStep()` 驱动贴图混合的方案。也可驱动灯光、颜色或效果强度；本次无法确认作者选择哪一种。

**项目建议**：为日夜内容提供自动时间、手动预览和固定主题三种状态；覆盖午夜衔接与暂停恢复。PC 与移动端的功能兼容应单独验证，作者的提示只代表这个作品报告过的情况。

### 3.2 暮霭海畔 星火寄梦：视差

[作品页](https://steamcommunity.com/sharedfiles/filedetails/?id=3463280673)

**页面事实**：标题标注 Parallax；Scene、4K，并有 Puppet Warp、音频响应、自定义、视频纹理标签；原画师署名画师 JW。本次读取说明，未查看该作品动态图。

**实现候选**：官方 [图层视差](https://docs.wallpaperengine.io/en/scene/parallax/introduction.html)和 [深度视差](https://docs.wallpaperengine.io/en/scene/parallax/depthparallax.html)分别支持不同制作路径。标题无法判断使用分层位移还是深度图。

**项目建议**：先做少量前、中、后景的微幅视差；检查被遮挡背景是否补全、人物边缘是否拉伸、最大位移是否露边。视差强度与关闭选项应可调，不能只靠加大移动幅度表达深度。

### 3.3 安和昴486：角色互动

[作品页](https://steamcommunity.com/sharedfiles/filedetails/?id=3448290956)

**页面事实**：标题写明多种互动，Scene、4K，具有 Puppet Warp、音频响应和自定义标签；作者感谢 Wiste_Finch 对项目的支持。页面约 13,180 次评分。

**预览观察**：人物正面靠近桌沿，脸部和手部是明确焦点。预览展示的是较小的方形画面，外侧黑色区域属于 Workshop 展示框，不能据此断言壁纸运行时具有黑边。没有实际点击角色，不能确认摸头、跟随、表情切换或具体触发区域。

**实现候选**：SceneScript cursor 事件、图层变换、时间轴与 Puppet Warp 均可用于角色反馈。官方文档中 cursor 事件需要相应对象设为 Solid；具体行为仍需项目源码或运行验证。

**项目建议**：定义一个清晰触发点和有限反馈动作，测试连续点击、离开区域和重复触发。角色互动应配合主画面节奏，并允许关闭。

### 3.4 其他值得保留的样本

| 作品 | 已确认的信息 | 可学习方向 |
| --- | --- | --- |
| [Alone 孤独の少女](https://steamcommunity.com/sharedfiles/filedetails/?id=3448877775) | 描述明确“时间变化设置、壁纸组件设置”，原画 Rella | 时间与组件设置共同存在，适合研究属性组织 |
| [初音未来：醉花间](https://steamcommunity.com/sharedfiles/filedetails/?id=3492627662) | Scene，自定义、媒体集成、Puppet Warp；说明原画为 AI 生成 | 原画来源与动态制作者分别记录 |
| [雨天的午后](https://steamcommunity.com/sharedfiles/filedetails/?id=3414858021) | 标题可互动，原画ゆづあ，Scene | 氛围内容与互动组合；具体互动尚未确认 |
| [水墨天南 踏雪驭龙](https://steamcommunity.com/sharedfiles/filedetails/?id=3509806978) | 标题 32:9—16:9，标签 Dual 7680×2160，原画 wlop | 用同一主题处理宽屏构图，不能把分辨率标签当作完整多屏适配证明 |
| [芙莉莲：轻涟与水之语](https://steamcommunity.com/sharedfiles/filedetails/?id=3538758087) | 说明最终成品融合两幅画，分别署名 yuu-zy 与 nana | 合成制作需要统一光照、色彩与空间关系；融合效果未逐项检查 |
| [源・Among the Roots](https://steamcommunity.com/sharedfiles/filedetails/?id=3494800319) | Landscape、Scene、媒体集成，原画 owenxc | 角色之外的风景方向，尚未观看其视频 |
| [时间轴动画速度调整](https://steamcommunity.com/sharedfiles/filedetails/?id=3447534673) | Asset / Script / Vec3；说明创建滑块控制时间轴动画速度 | 作者也发布可复用脚本，制作经验可以沉淀成素材 |

## 4. Hope 麻匪：桌面组件与实时效果

### 4.1 Dock / Dock2：一套可配置的桌面组合

[Dock2](https://steamcommunity.com/sharedfiles/filedetails/?id=3599498791) · [Dock](https://steamcommunity.com/sharedfiles/filedetails/?id=3577513994) · [演示视频](https://www.youtube.com/watch?v=JHe0GYENWxQ)

**页面事实**：两件作品都标注 Scene、4K、Audio responsive、Customizable、Media Integration、User Shortcut。两页说明 Dock 栏由 seekay 开发并链接同一素材；Dock 还署名日历 UI 来源 OnlineTutorials。不能因为画面像前端组件就将作品判定为 Web。

**视频抽样观察**：

- 约 10 秒：角色背景上组合数字时间、日期卡片、媒体卡片和底部应用图标栏。
- 约 26 秒：灰色背景上的红色媒体卡片，同时展示属性面板。可见媒体开关、大小、混合模式、主副颜色、封面 Logo 等设置；卡片有前一首、暂停、后一首图标。
- 约 42 秒：展示日期卡片的设计初稿，星期列与较大的日期信息形成层级。

观察证明这些设计出现在演示中；没有亲自操作播放按钮、快捷图标或拖拽，不据此声称它们在当前版本和所有播放器中均可用。静态预览单独主要展示 Dock 图标条，视频给出了更完整的组件组合。

**实现候选**：场景文字、图像、属性与脚本即可组织上述组件。媒体元数据与控制可参考官方 [Scene 媒体集成](https://docs.wallpaperengine.io/en/scene/audiovisualizer/mediainformation.html)，启动入口可参考 [User Shortcut](https://docs.wallpaperengine.io/en/scene/userproperties/usershortcut.html)。每次用户点击只能按文档触发一次快捷方式，绑定具体机器，不随跨机器预设共享。

**项目建议**：将背景、日期、媒体、快捷栏各自设为可开启模块；大小、位置、主题与数据状态分别管理。没有封面、没有媒体会话、无效快捷路径时要有稳定显示和明确反馈。组件配置导出时不能默认在另一台机器上存在相同路径。

### 4.2 磁流体：已明确说明技术组合

[作品页](https://steamcommunity.com/sharedfiles/filedetails/?id=3666041758)

**页面事实**：作者直接写明“利用 Shader 着色器代码实现液体动态，使用 SceneScript 代码进行交互制作”。分类为 Scene，具有音频响应和自定义标签。

这比从效果名称推测技术更可靠。它确认 Shader 与脚本的组合，但没有公开本次已读取的具体算法。不能认定它实现了物理准确的流体求解、SPH 或某种固定噪声模型。

**项目建议**：脚本负责输入、状态与参数，Shader 负责连续视觉计算；音频先平滑、限幅后映射到少数参数。首先验证视觉稳定、可调性与成本，再决定是否需要物理模拟。参考官方 [Shader 概述](https://docs.wallpaperengine.io/en/scene/shader/overview.html)和 [SceneScript 音频教程](https://docs.wallpaperengine.io/en/scene/scenescript/tutorial/audio.html)。

### 4.3 3D 布料模拟：Web 是另一条路线

[作品页](https://steamcommunity.com/sharedfiles/filedetails/?id=3709882938) · [视频入口](https://www.youtube.com/watch?v=KcKEp04BTBo)

**页面事实**：类型 Web，Dynamic resolution，可自定义；作者说明借助 Codex + GPT5.4、Agent + Skills 构建 Web 前端，并完成 Wallpaper Engine 属性交互。

**预览观察**：浅蓝色背景中央是一张白边图像，边缘弯曲，有投影，构图集中于单个物件。静态图不能确认布料的碰撞、约束、拖拽和数值稳定性。作者也没有在说明里明确 Three.js、物理引擎或自定义求解器名称。

**实现候选**：WebGL 场景与顶点变形或布料求解器，配合 `window.wallpaperPropertyListener.applyUserProperties` 接收宿主设置。参考官方 [Web 属性](https://docs.wallpaperengine.io/en/web/customization/properties.html)。

**项目建议**：Web 原型适合验证代码生成、视觉计算和宿主属性桥接；暂停、缩放、长时间运行、像素密度与模拟步长都需要真实测试。页面显示 3.428 MB，仅说明资源包大小，不能推断它比 Scene 更省 GPU。

### 4.4 音频与媒体不是同一种输入

| 样本 | 已确认的信息 | 学习重点 |
| --- | --- | --- |
| [3D磨砂 音频识别 Media Player](https://steamcommunity.com/sharedfiles/filedetails/?id=3708363772) | Scene、3D、Audio responsive、Media Integration；明确链接 Utopia 为原项目素材 | 用来源链研究媒体封面、配色与音频，而不是仅凭标题理解“识别” |
| [模型音频互动](https://steamcommunity.com/sharedfiles/filedetails/?id=3617056047) | Scene、3D、Audio responsive | 模型参数与音频结合；具体算法未公开 |
| [音频交互 雕像](https://steamcommunity.com/sharedfiles/filedetails/?id=3645009840) | Scene、Puppet Warp、3440×1440 | 音频也能驱动形变类内容 |
| [桌面快捷启动](https://steamcommunity.com/sharedfiles/filedetails/?id=3570755196) | Scene、User Shortcut、Video Texture | 启动入口与背景运动可以组合 |
| [棘 三模式切换](https://steamcommunity.com/sharedfiles/filedetails/?id=3773747576) | 标题三模式；Scene、自定义、音频、Puppet Warp | 预览只看见黑白角色画，三模式的差异和切换机制未确认 |

**接口边界**：音频频谱用于节奏、强度与形变；媒体会话用于歌名、封面、播放状态与控制。标题中的“音频识别”不能证明识曲、语音识别或指定播放器捕获。官方帮助明确音频分析不能直接限定到某一播放器，另有虚拟音频设备方案。见 [音频来源限制](https://help.wallpaperengine.io/en/audio/limittomusicplayer.html)及 [媒体集成](https://help.wallpaperengine.io/en/audio/mediaintegration.html)。

## 5. 沿引用链学习：seekay 与 JPL

### 5.1 App Launcher Dock：可复用资产

[素材页](https://steamcommunity.com/sharedfiles/filedetails/?id=3573886911)

**页面事实**：作者 seekay_labs，Category 为 Asset，Asset Type 为 Image，Asset Genre 为 Interactive。说明是使用 user shortcut properties 的 Apple 风格应用启动栏。这是素材分类，不能误写成独立 Web 壁纸。

**预览观察**：同一图标组展示横向、纵向和不同尺寸布局；部分图标比相邻图标大。静态图不证明鼠标悬停放大过程。麻匪 Dock/Dock2 的明确署名说明这种素材可以作为更大作品的组成部分。

**项目建议**：为组件规定输入属性、布局参数、交互事件、默认值和版本信息。素材包与整件壁纸分别记录来源和依赖。

### 5.2 Utopia：让媒体数据成为画面主题

[作品页](https://steamcommunity.com/sharedfiles/filedetails/?id=2970648387)

**页面事实**：作者 seekay；Scene、Media Integration、Audio responsive、Customizable。说明背景使用当前专辑封面颜色，支持可选音频可视化、效果、黑胶唱片视觉；作者声明 1:1 到 16:9 比例使用 fill 应有良好效果。还署名 Gariams 的 Procedural Noise Effect。

**预览观察**：主图以居中封面和外框作为焦点，背景颜色与封面相协调；缩略图显示多张封面和黑胶样式。媒体换歌和自动取色依据来自说明，尚未实测。比例支持范围也是作者声明。

**项目建议**：封面颜色驱动背景主题，但文字对比度、无封面占位与换歌过渡由组件规则保证；不要每帧重新加载封面或计算全图颜色。

### 5.3 漂 雲2 决定版：氛围与合作署名

[作品页](https://steamcommunity.com/sharedfiles/filedetails/?id=3673609113)

**页面事实**：作者区同时列出 Hope 麻匪与 JPL；Scene、音频响应、自定义、Puppet Warp、HDR。说明署名原画作者しっぺい次郎、列出音乐并提到修复穿模 BUG。

**预览观察**：夕阳、草地和侧坐吹奏人物，逆光轮廓和暖色统一画面。HDR 标签无法证明当前 SDR 浏览器里的真实 HDR 观感，也没有检查穿模修复效果。

**项目建议**：视觉设计需要与形变边界共同验证；原画作者、动态制作者、合作作者、音乐和素材应分别署名。

## 6. 对本项目的可执行建议

### 6.1 三个原型足以先验证主要方向

| 原型 | 内容与输入 | 要验证的能力 | 验收观察 |
| --- | --- | --- | --- |
| 氛围场景 | 自有或获授权原画；三层视差；日夜主题 | 图层、坐标、时间、主题属性 | 最大视差不露边；午夜无闪变；暂停恢复后时间正确；16:9 和超宽屏焦点保留 |
| 组件桌面 | 背景 + 日期 + 媒体 + 一排快捷入口 | 属性持久化、模块配置、媒体桥接、用户触发 | 无会话/封面时正常显示；按钮状态明确；组件可关闭；重启恢复布局；失效路径有反馈 |
| 音频效果 | 单个物件或 Shader 效果 | 频谱、平滑、限幅与运行策略 | 静音稳定；音频切换无剧烈跳变；输入停止后回落；暂停时计算按策略停止 |

这是建议顺序，不是技术栈决定。如果目标是制作 Wallpaper Engine 内容，可直接围绕 Scene/Web 生态验证；如果目标是开发独立桌面壁纸软件，还需实现宿主、桌面输入、媒体桥接、调度与内容分发。创作者案例不能替代宿主架构设计。

### 6.2 设计与制作层面的共通经验

1. **先确定焦点**：伊蕾娜用光色和清晰度组织人物；Utopia 用封面组织信息。动画和组件围绕焦点安排。
2. **将可调能力变成属性**：速度、强度、主题、组件开关是内容适应不同使用者的入口；不必把每个内部变量都暴露。
3. **设置稳定的默认状态**：无音乐、无网络、无媒体会话或没有快捷绑定时，壁纸仍应可用。
4. **内容与组件可复用**：夜莺的速度脚本与 seekay 的 Dock 素材提供了实际例子。来源、依赖与版本需要可追溯。
5. **多屏需要构图规则**：4K、超宽和双宽标签体现需求，但运行时的裁剪、缩放、输入映射仍要测试。
6. **真实性能另行测量**：Elaina 页面为 337.247 MB，角色互动为 16.241 MB，布料 Web 为 3.428 MB。文件体积不同不能推出运行成本排名。

建议性能测试至少记录内容类型、显示分辨率、目标 FPS、CPU/GPU、显存与内存、活跃效果，以及前台应用触发暂停前后的变化。暂不设没有硬件依据的统一资源上限。

### 6.3 研究中的素材边界

夜莺部分作品明确限制未经允许商用与跨平台转载。页面可公开查看不代表原画、音乐、脚本或成品可直接复用。当前研究学习功能组织和制作方法；原型使用自有或获授权素材，具体复用前核对对应许可。

## 7. 结论与尚待验证项

两位指定创作者提供了两条互补方向：夜莺的样本适合学习分层画面与氛围交互；麻匪的样本适合学习模块组合、媒体输入与不同运行类型。seekay 素材引用链进一步说明，创作生态需要可复用资产和清晰署名。

当前已完成公开目录、代表作品说明和指定预览的研究。以下结论仍需本地项目或源码验证：实际属性全集、点击区域与事件处理、动画曲线、使用的 JS/物理库、流体算法、媒体兼容性、暂停行为、跨屏适配和运行性能。后续原型应优先验证这些问题。
