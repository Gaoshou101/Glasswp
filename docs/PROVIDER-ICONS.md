# AI Agent 官方本地图标库

核验日期：2026-11-11（Asia/Shanghai）。覆盖现有接入清单全部 69 个 Magpie provider ID，以及本机/CPAMP 的 Codex、Claude、Gemini、Copilot 等别名；共 58 个品牌键。图标支持不表示原生额度接口支持，接入边界仍以 AI-SOURCES-2026-11-10.md 为准。

资源位于 `public/assets/providers/`，构建复制到 `dist/assets/providers/`，显示时无需联网。`manifest.json` 保存逐项来源、真实尺寸、SHA256、别名及处理说明。保留官方原色；不使用插件自绘占位图，不将小位图套入 SVG 冒充矢量，不做像素放大。

统一为 28×28 设计像素底板、22×22 内容区、`object-fit: contain`；原始宽高比不变。Kimi、Copilot、Cline 等黑色图形用浅底，Warp/七牛用深底；Antigravity 继续使用透明彩色标志。CSS 品牌键由父级 `data-brand` 控制，地区/CLI/桌面别名共用品牌图标。

大多数位图为 144–1646px 原图；CatPaw 110×108、TeleAgent 116×116、MClaw 80×80 使用原厂现有尺寸，分别足以覆盖约 5×、5×、3.6× 的 22px 显示密度，未声称它们是超大原图。

| 品牌 | 本地文件 | 原始尺寸/矢量画布 | 官方来源 |
| --- | --- | --- | --- |
| Codex | `codex.svg` | 矢量 0 0 180 180 | [原始来源](https://chatgpt.com/cdn/assets/favicon.6a33781f61.svg) |
| Claude | `claude.svg` | 矢量 0 0 248 248 | [原始来源](https://claude.com/icon.svg) |
| Antigravity | `antigravity.svg` | 矢量 0 0 136 136 | [原始来源](https://antigravity.google/favicon.svg) |
| Kimi | `kimi.svg` | 矢量 0 0 24 25 | [原始来源](https://moonshotai.github.io/Branding-Guide/scenarios/04-k-only/k-only-dark.svg) |
| xAI | `xai.png` | 512×512 | [原始来源](https://docs.x.ai/_next/static/media/icon.2jebtfdg6783w.png?dpl=f42878fa3449c4ffee9e3c02fac8a11736523c68) |
| Devin | `devin.svg` | 矢量 0 0 425 425 | [原始来源](https://devin.ai/favicon.svg) |
| Meta | `meta.svg` | 矢量 0 0 32 32 | [原始来源](https://static.xx.fbcdn.net/rsrc.php/yf/r/-7pQO6hUGK_.svg) |
| GitHub Copilot | `github-copilot.svg` | 矢量 0 0 24 24 | [原始来源](https://raw.githubusercontent.com/primer/octicons/main/icons/copilot-24.svg) |
| Gemini | `gemini.png` | 512×512 | [原始来源](https://www.gstatic.com/lamda/images/gemini_sparkle_4g_512_lt_f94943af3be039176192d.png) |
| Cursor | `cursor.png` | 256×256 | [原始来源](https://cursor.com/favicon.ico) |
| WorkBuddy | `workbuddy.svg` | 矢量 0 0 40 40 | [原始来源](https://download.codebuddy.ai/web/workbuddy/05fd33c3609bc9cf5ae1fc387a142a2ad0fab64e/assets/logo.svg) |
| Trae | `trae.svg` | 矢量 0 0 48 35 | [原始来源](https://lf16-web-neutral.traecdn.ai/obj/trae-ai-static/trae_website/static/js/main.cec28a9f4b.js) |
| Kiro | `kiro.svg` | 矢量 0 0 1200 1200 | [原始来源](https://kiro.dev/icon.svg?fe599162bb293ea0) |
| ZCode | `zcode.png` | 192×192 | [原始来源](https://zcode.z.ai/favicon-192x192.png?v=20260707-transparent) |
| Command Code | `commandcode.png` | 180×180 | [原始来源](https://commandcode.ai/apple-touch-icon.png) |
| Grok | `xai.png` | 512×512 | [原始来源](https://docs.x.ai/_next/static/media/icon.2jebtfdg6783w.png?dpl=f42878fa3449c4ffee9e3c02fac8a11736523c68) |
| CatPaw | `catpaw.png` | 110×108 | [原始来源](https://s3plus.meituan.net/catpaw-external-resources/login/logo.png) |
| 商汤小浣熊 | `raccoon.svg` | 矢量 50 5 200 200 | [原始来源](https://sta-volc.xiaohuanxiong.com/raccoon-website/prod/mime-v1/favicon.svg) |
| AtomCode | `atomcode.svg` | 矢量 0 0 24 24 | [原始来源](https://raw.githubusercontent.com/atomgit-atomcode/atomcode/main/extensions/vscode/resources/icon.svg) |
| Warp | `warp.svg` | 矢量 0 0 37 35 | [原始来源](https://www.warp.dev/img/logos/warp-pixel-icon.svg) |
| Agnes | `agnes.png` | 1134×1134 | [原始来源](https://www.agnes-ai.com/images/biglogo.png) |
| Agnes Code | `agnes-code.png` | 1134×1134 | [原始来源](https://agnes-ai.com/images/biglogo.png) |
| AStudio | `astudio.svg` | 矢量 0 0 570 570 | [原始来源](https://agent.xfyun.cn/studio-portal/assets/logo-B-DIhbmk.svg) |
| Codar | `codar.png` | 256×256 | [原始来源](https://codar.speedai.com/assets/codar-icon.png) |
| DeepAI Code | `deepaicode.png` | 256×256 | [原始来源](https://deepai-code-downloads.kmbaragona.workers.dev/deepai-code/releases/DeepAI-Code-0.3.35-x64.exe) |
| TeleAgent | `teleagent.jpg` | 116×116 | [原始来源](https://cdn02.teleai.com.cn/ssr_static/img/logo.CZ4CmPHw.jpg) |
| AutoClaw | `autoclaw.png` | 180×180 | [原始来源](https://autoclaw.zhipuai.cn/apple-touch-icon.png) |
| Qoder | `qoder.svg` | 矢量 0 0 206 206 | [原始来源](https://qoder.com/favIcon.svg) |
| Cline | `cline.png` | 180×180 | [原始来源](https://cline.bot/assets/branding/favicons/apple-touch-icon.png) |
| MiniMax Code | `minimax.jpg` | 460×460 | [原始来源](https://github.com/MiniMax-AI.png?size=512) |
| OpenCode | `opencode.svg` | 矢量 0 0 300 300 | [原始来源](https://opencode.ai/brand/files/opencode-logo-dark-square.svg) |
| Zed | `zed.png` | 1646×1646 | [原始来源](https://zed.dev/_next/static/media/stable-app-logo.0lgsg40_u_1r5.png) |
| Factory | `factory.svg` | 矢量 None | [原始来源](https://factory.ai/favicon.svg) |
| 豆包工作 | `doubao.png` | 192×192 | [原始来源](https://lf-flow-web-cdn.doubao.com/obj/flow-doubao/favicon/new-doubao/192x192.png) |
| SenseNova | `sensenova.png` | 300×300 | [原始来源](https://www.sensenova.ai/apple-icon.png) |
| 书生·端砚 | `inkstone.svg` | 矢量 0 0 32 32 | [原始来源](https://static.intern-ai.org.cn/platform-config-upload/discoverypublic/tab_logo_1789096347416.svg) |
| 讯飞星辰 | `xfyun.svg` | 矢量 0 0 25 25 | [原始来源](https://maas.xfyun.cn/svg/common-logo-13ec678e.svg) |
| Muse | `meta.svg` | 矢量 0 0 32 32 | [原始来源](https://static.xx.fbcdn.net/rsrc.php/yf/r/-7pQO6hUGK_.svg) |
| Tabbit | `tabbit.png` | 160×160 | [原始来源](https://web.tabbit.com/favicon.ico) |
| AWS Bedrock | `aws.png` | 144×144 | [原始来源](https://a0.awsstatic.com/libra-css/images/site/touch-icon-ipad-144-smile.png) |
| Cloudflare | `cloudflare.svg` | 矢量 0 0 341 156 | [原始来源](https://www.cloudflare.com/icons.svg) |
| Google Cloud | `gcp.png` | 192×192 | [原始来源](https://www.gstatic.com/cgc/super_cloud_gradient.png) |
| 七牛云 | `qiniu.svg` | 矢量 0 0 33 22 | [原始来源](https://www.qiniu.com/) |
| Loomy | `loomy.png` | 1024×1024 | [原始来源](https://loomy.xunfei.cn/icon.png?5f7e7b8be5d4c76b) |
| JoyCode | `joycode.png` | 654×654 | [原始来源](https://JoyCoder.gallerycdn.vsassets.io/extensions/joycoder/joycoder-fe/3.8.71/1789738628706/Microsoft.VisualStudio.Services.Icons.Default) |
| Xiaomi MiMo | `mimo.png` | 535×534 | [原始来源](https://platform.xiaomimimo.com/favicon.png) |
| Nous Portal | `nous.svg` | 矢量 0 0 5487.0615 5558.8086 | [原始来源](https://raw.githubusercontent.com/NousResearch/hermes-agent/main/assets/nous-girl-white.svg) |
| OrcaTerm | `orcaterm.svg` | 矢量 0 0 100 100 | [原始来源](https://cloudcache.tencent-cloud.com/qcloud/orcaterm/website/favicon.d6ced2b3.svg) |
| MClaw | `mclaw.png` | 80×80 | [原始来源](https://yun.139.com/w/static/img/LOGO.png) |
| River | `river.png` | 192×192 | [原始来源](https://river.ai/assets/favicon-192.png?v=2) |
| Open Design | `opendesign.png` | 180×180 | [原始来源](https://r.open-design.ai/files/f6843349fba478ad7249f9c71cc5ae0a9ff9986b081b5b3a40eb8e0e86eadd5c) |
| CodeArts | `codearts.svg` | 矢量 0 0 48 48 | [原始来源](https://res-static.hc-cdn.cn/cloudbu-site/intl/zh-cn/codearts/logo.svg) |
| OfficeAce | `officeace.png` | 176×176 | [原始来源](https://res-static.hc-cdn.cn/cloudbu-site/china/zh-cn/智果AgentArts/OfficeAce/新图标.png) |
| SAP AI Core | `sap.svg` | 矢量 0 0 92 45 | [原始来源](https://raw.githubusercontent.com/SAP/openui5/master/src/sap.m/test/sap/m/demokit/sample/Image/images/sap-logo.svg) |
| Accio | `accio.svg` | 矢量 0 0 24 24 | [原始来源](https://s.alicdn.com/@img/imgextra/i4/O1CN01L3EOoU1IfPQXGpYhJ_!!6000000000920-55-tps-24-24.svg) |
| LobsterAI | `lobsterai.png` | 192×192 | [原始来源](https://lobsterai.youdao.com/icon-192.png) |
| MagiCrew | `magicrew.svg` | 矢量 0 0 459 459 | [原始来源](https://www.magicrew.ai/web/favicon-intl.svg) |
| MonkeyCode | `monkeycode.png` | 600×602 | [原始来源](https://monkeycode-ai.com/logo-light.png) |

## 处理与验证

官方 SVG 中的独立图形提取：Cloudflare 保留彩色组，TRAE 从官网 JS 内嵌原始 SVG 提取，七牛移除单独字标并收紧画布，讯飞星辰仅裁切官方 header 的图形区域。AStudio 删除 Figma 导出的 foreignObject 背景模糊占位，保留图形路径和渐变。DeepAI PNG 从官网安装包的 RT_ICON 只读提取，未执行安装程序。

`npm run check` 验证全量映射、资源安全、69-provider 文档覆盖和构建；`tests/provider_icons_check.py` 用 Chromium 离线加载全部品牌，检查 1×/2× 像素密度、统一尺寸、无破图及无网络取图。使用本仓库 `.tools/python` 时需 CPython 3.12。预览位于 `artifacts/validation/provider-icons/`。

各品牌商标与图形权利属于各自权利人，资源仅用于标识用户已接入服务，不代表任何品牌背书。此次仅更新开发目录图标库和构建包，未安装到正式桌面运行目录。
