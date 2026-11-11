# 世界时钟、显示校准与天气来源

日期：2026-11-10。此记录对应开发实现，安装状态以实际发布记录为准。

## 使用

- 点击顶部时间卡片展开世界时钟，再次点击、点击空白或按 Escape 收起；也支持键盘 Enter / 空格。与天气、额度和硬件详情共用浮层。
- 世界时钟显示各地时间、UTC 偏移和相对主时区的日期差，按实际日期处理夏令时，支持半小时与 45 分钟时区。
- 管理页「时间与世界时钟」接受合法 IANA 时区，如 `Asia/Shanghai`、`America/New_York`、`Asia/Kathmandu`。世界列表最多 24 个，主时区始终显示。
- 秒、12/24 小时制与校准值随管理配置同步到连接的壁纸。首次点击「保存时间设置」才启用管理配置；旧配置和旧桥继续使用原本的本地显示设置。
- 手动校准为 ±86400 秒以内的偏移，可填写小数。网络校时从固定 Cloudflare HTTPS 端点读取 Date 响应头，并补偿往返时间的一半；这是约 1 秒精度的显示校准，结果先进入草稿，点击保存后生效。失败保留原设置。
- 校准只影响主时间与世界时钟；不改变 Windows 系统时间、天气有效期、额度重置时刻或请求记录。

## 天气来源

| 来源 | 凭据 | 说明 |
| --- | --- | --- |
| Open-Meteo | 无 | 原有天气模型，实况、日范围与逐小时预报 |
| MET Norway | 无 | 预报模型；按服务器 Expires 复用预报，日范围为预报采样范围 |
| OpenWeatherMap | API Key | 当前天气与三小时预报；日范围为当日余下预报采样范围，预报失败可保留实况 |
| WeatherAPI | API Key | 当前天气、日范围与逐小时预报 |

城市独立选择，沿用 Open-Meteo 地理检索。带凭据的来源需要相应服务账号及可用接口权限；集成不会自动申请账号。API Key 在本机 DPAPI 配置中加密存储，不返回展示快照或管理页。相同来源留空保留已存密钥；切换来源不会沿用其他来源的密钥；主动清除密钥后显示未配置。

天气失败只保留同一来源、同一城市坐标与时区的缓存；切换来源或城市不混用缓存，旧请求返回时不会覆盖新来源。不同来源的预报分辨率按原始数据呈现，不插值伪造逐小时数据。

官方接口依据：

- https://open-meteo.com/en/docs
- https://docs.api.met.no/doc/locationforecast/HowTO.html
- https://openweathermap.org/current
- https://openweathermap.org/forecast5
- https://www.weatherapi.com/docs/

## 验证入口

- `npm run check`：前端单元测试、类型检查、生产构建。
- `WallpaperBridge.Tests`：配置边界、校时响应、天气解析与请求、来源缓存及异步切换等，天气及校时使用模拟响应。
- `tests/world_clock_check.py`：世界时钟实际浏览器交互、布局、暂停和桌面输入保护。
- `tests/clock_weather_admin_check.py`：管理页保存、校时草稿、来源凭据、错误与窄屏布局。
- `tests/display_settings_api_check.py <dotnet.exe>`：独立临时数据目录和随机端口，验证真实管理 API 鉴权、配置往返、凭据隔离、加密重启恢复。
- `scripts/verify.ps1 -Browser -Python <Python3.12>` 已纳入上述新增验收。

本轮不会用用户的天气 API Key 进行付费来源实网测试；模拟响应测试与实际供应商账号验证是不同范围。

## 本轮结果

- 候选构建：`20261110055739-a35b501f`，目录 `artifacts/candidates/20261110055739-a35b501f`；前端包位于 `dist`，桥接候选同时放入 `artifacts/staging`。基于现有未提交视觉改动构建，未安装到正式运行目录或 Wallpaper Engine。
- 33 项前端单元测试、类型检查、生产构建与 368 项桥接检查通过。
- 新世界时钟浏览器检查、新管理设置 8 组检查、原管理页 15 组回归通过；桌面点击保护、快照生命周期、液态玻璃详情和视觉回归通过，无浏览器错误。
- 独立管理 API 验证通过：鉴权、配置读取保存、非法值拒绝、密钥不回显与不跨来源、清除密钥、重启恢复；真实 HTTPS 校时成功且未改变配置。
- 候选发布清单与文件哈希验证通过；隔离桥接启动失败回退验证通过，旧运行文件、DPAPI 配置、展示票据和硬件采样均恢复，正式运行环境未改动。
- MET Norway 苏州实网请求 HTTP 200；OpenWeatherMap 与 WeatherAPI 使用模拟响应验证，尚未使用真实密钥验证用户账号权限。
- 预览截图：`artifacts/validation/world-clock/world-clock.png`、`artifacts/validation/clock-admin-1200.png`、`artifacts/validation/weather-admin-390.png`。
