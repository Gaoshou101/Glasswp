# Glasswp 创意工坊三套包

三套壁纸复用相同组件与桥接。每套包在 HTML 根元素写入 `data-wallpaper-preset`，由正式前端固定该包的主题，不依赖浏览器缓存，也不改写打包后的 JavaScript。

| 工坊标题与输出目录 | 固定预设 | 背景 |
|---|---|---|
| `artifacts/Glasswp-Purple` | `original` | 紫色角色原图 |
| `artifacts/Glasswp-Angel` | `neon` | 黄绿色女孩原图 |
| `artifacts/Glasswp-Corgi` | `pool` | 泳池柯基原图 |

## 生成和验证

先执行 `npm run build`，再执行 `node scripts/prepare-workshop-variants.mjs` 和 `python tests/workshop_package_check.py`。

生成脚本先验证 `dist/build-info.json` 的全部文件哈希，要求构建包含固定主题契约，再在临时目录生成每套包并检查。脚本不会递归删除目录；已有同名未发布包会保留为带唯一后缀的备份。已有目录如果包含工坊发布 ID 字段（即使为空），则拒绝覆盖。

浏览器验收脚本为 `python tests/workshop_browser_check.py`，需要当前 Python 对应的 Playwright 依赖与本机 Chrome。它从本地文件直接打开三套包，分别检查空票据、缓存主题冲突、桥接主题冲突及重新加载，确认主题、背景和明暗模式保持固定。所有桥接请求都由匿名测试数据拦截；截图和结果写入 `artifacts/validation/workshop-packages`。

通过检查后执行 `python scripts/zip-workshop-variants.py`，生成 `artifacts/Glasswp-Purple.zip`、`artifacts/Glasswp-Angel.zip`、`artifacts/Glasswp-Corgi.zip`。ZIP 保留各自同名顶层目录；打包前核验目录文件清单和 SHA256，打包后重新读取 ZIP 核验全部文件和内容。`artifacts/Glasswp-workshop-zips.json` 记录三个压缩包的哈希、文件数及来源构建 ID。

包内的 `bridge_token` 会明确清空，全部 Workshop / Published File ID 字段都会删除。预览图为对应背景原画的真实 JPEG，名为 `preview.jpg`，不包含账号、用量或设备信息。`package-info.json` 记录每个文件的 SHA256 与来源构建 ID。

## 本人手动上传

1. 在 Wallpaper Engine 壁纸编辑器中分别打开三个目录的 `project.json`。
2. 选择发布到创意工坊，标题保持 `Glasswp-Purple`、`Glasswp-Angel`、`Glasswp-Corgi`，预览图使用包内 `preview.jpg`。
3. 本包默认私人，可按需要修改可见性。原图权利归原作者所有；发布和转载时应保留署名及授权信息。
4. 发布完成后保留 Wallpaper Engine 写入的工坊 ID，后续更新同一作品时使用该 ID。
5. 新的 Steam / 创意工坊协议需本人阅读并确认。

每包提供中文 `使用说明.txt`。日期与时钟独立运行；天气、AI 额度和硬件数据需安装并启动 Glasswp / WallpaperBridge，然后在本机管理页配置来源，将展示票据填入 Wallpaper Engine 属性。管理凭据只在本机填写。

本脚本只准备发布包，不上传、不修改已发布工坊项目，也不改变当前桌面壁纸。
