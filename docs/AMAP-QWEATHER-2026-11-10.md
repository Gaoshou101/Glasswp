# 高德与和风天气接口核对（2026-11-10）

高德、和风功能已于 2026-11-10 安装到正式桥接与壁纸。开发与验收使用隔离配置，没有改写用户的天气配置，也没有使用真实 API Key 调用收费来源。

## 高德天气

- 官方文档：https://lbs.amap.com/api/webservice/guide/api/weatherinfo
- 固定 HTTPS 端点 `https://restapi.amap.com/v3/weather/weatherInfo`；需要 Web 服务类型 Key 和城市 `adcode`。管理页必须明确填写 6 位行政区编码，避免把其他来源的城市 ID 误作高德编码。
- `extensions=base` 返回 `lives[]` 实况，`extensions=all` 返回 `forecasts[].casts[]` 每日预报。成功同时校验 `status=1`、`infocode=10000`；匹配返回的 `adcode` 后才使用天气。
- 实况字段包括 `city`、`province`、`weather`、`temperature`、`reporttime`。温度可能是字符串；无时区的 `reporttime` 按中国标准时间 UTC+08:00 解析。
- 高德日间/夜间预报温度来自当天 `casts[].daytemp` 与 `nighttemp`；界面明确这是日间/夜间温度范围。高德没有这个接口下的小时预报，本实现返回空小时列表，不推算或补造数据。
- 显示城市采用高德真实返回的城市、省份及 adcode；不以另一个来源的经纬度城市标签替代。

## 和风天气

在本次核对时，旧的天气文档入口已经指向 Weather v1；官方 v7 文档仍存在，但明确提示即将弃用。本实现采用现行 v1，不在失败时盲目切换版本，避免掩盖鉴权或套餐问题并增加请求。

官方资料：

- 实时：https://dev.qweather.com/docs/api/weather/weather-current/
- 每日：https://dev.qweather.com/docs/api/weather/weather-daily-forecast/
- 小时：https://dev.qweather.com/docs/api/weather/weather-hourly-forecast/
- API 配置与 Gzip：https://dev.qweather.com/docs/configuration/api-config/
- API Host：https://dev.qweather.com/docs/configuration/api-host/
- 身份认证：https://dev.qweather.com/docs/configuration/authentication/
- 天气现象代码：https://dev.qweather.com/docs/api/weather/weather-conditions/
- 旧 v7 提示：https://dev.qweather.com/docs/api/weather/weather-now-webapi-v7/

实现约定：

- 用户填写自己控制台中的 API Host，接受一个或多个合法 ASCII DNS 标签加 `.qweatherapi.com` 后缀，包括 `demo.re.qweatherapi.com` 等区域主机。逐标签最多 63 字符、整体最多 253 字符；拒绝协议、路径、端口、用户信息、IP、其他域名、空标签或标签首尾连字符。
- 请求路径为 `/weather/v1/current/{latitude}/{longitude}`、`/weather/v1/daily/{latitude}/{longitude}?days=3`、`/weather/v1/hourly/{latitude}/{longitude}?hours=24`，经纬度保留官方支持的两位小数，并请求中文天气描述。
- API Key 只通过 `X-QW-Api-Key` 发给经校验的专属 HTTPS 主机，不放入 URL、快照、日志或文档。默认 HTTP handler 禁止自动重定向。
- 明确请求 `Accept-Encoding: gzip` 并解压；压缩响应和解压后的 JSON 均受 4 MiB 大小限制。
- v1 实时响应使用 `condition.code`、`temperature.value`，没有官方观测时间字段，故不把抓取时间伪造为观测时间。
- v1 每日响应使用 `days[].forecastStartTime/forecastEndTime` 匹配当前日期区间，并读取 `temperatureMin.value` / `temperatureMax.value`。小时响应为 `hours[].forecastTime`、`temperature.value`、`precipitation.amount.value` 和 `precipitation.probability`（0–1，显示时转为百分数）。
- 两个预报接口独立降级，某一接口不可用时仍保留可用实况和另一个预报；鉴权错误、错误 JSON、错误业务 code 或缺失实况温度均不能标成成功。
- `metadata.attributions` 作为来源说明文本保留，不作为自动访问或执行的地址。日出/日落可用时用于昼夜状态，否则按选定城市的本地小时近似判断图标。
- 官方认证页提示：自 2027-01-01 起将限制 API Key 认证的每日请求量。当前仍支持 API Key；JWT 不在本次实现范围。

## 缓存与验证边界

缓存身份包含来源、城市、坐标、时区、和风 API Host、高德城市 adcode，不包含密钥。来源切换、主机变化或 adcode 变化时不复用其他来源的天气；必需密钥清除后显示待配置。

回归使用 mock HTTP 和虚构 Key，覆盖真实官方响应结构、字符串温度、中国标准时间、天气码、独立预报失败、压缩响应、主机白名单和密钥隔离。没有付费 Key 的真实连通性与套餐权限需要使用者自己的控制台凭据才能验证。

## 保存失败修复

2026-11-10 用户报告合法区域 API Host 保存失败，页面提示「请填写有效和风 API Host」。根因是前后端错误地只允许单层子域名；官方 API Host 文档本身展示多层区域主机。修复放宽为上述合法域名标签规则，保留固定供应商后缀限制，以及 Host 改变必须重新提供密钥的隔离规则。测试使用虚构 `demo.re.qweatherapi.com`，覆盖管理页保存、后端配置往返及真实请求构造，不记录用户 Host 或 Key。

修复构建 `20261110083359-e47c1b94` 已安装至 `E:\WallpaperDashboard\current` 与当前 BOE1600 / UID524 的 `portrait-dashboard-workshop` 项目。438 项桥接检查、33 项前端测试、16 组管理页浏览器回归及独立配置 API 验证通过；正式页面的区域 Host 校验通过（模拟保存请求在浏览器内拦截，未写配置）。发布清单哈希通过，Wallpaper Engine 已上报同一新构建，服务仍提权且自启动任务 Running，12 个账号保留。当前用户选择的天气来源原样保留。
