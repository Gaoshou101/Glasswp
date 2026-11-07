# 竖屏动态信息壁纸研究来源

研究日期：2026-10-10。主方案：`docs/PORTRAIT-DASHBOARD-PROPOSAL.md`。

## 官方文档

| 来源 | 本轮核对的内容 |
| --- | --- |
| [WE Web 概览](https://docs.wallpaperengine.io/en/web/overview.html) | Web 壁纸与视频格式范围 |
| [WE 用户属性](https://docs.wallpaperengine.io/en/web/customization/properties.html) | 可配置设置与宿主属性 |
| [WE 属性事件](https://docs.wallpaperengine.io/en/web/api/propertylistener.html) | 用户属性、FPS、暂停通知 |
| [CPAMP 官方仓库](https://github.com/seakee/CPA-Manager-Plus) | Full Mode、管理与配额能力 |
| [CPAMP 账号文档](https://seakee.github.io/CPA-Manager-Plus/docs/en/manual/accounts.html) | 配额来源、刷新策略、账号关联 |
| [CPAMP 能力矩阵](https://seakee.github.io/CPA-Manager-Plus/docs/en/reference/capability-matrix.html) | Provider 支持差异与未知字段边界 |
| [CPAMP 运行模型](https://seakee.github.io/CPA-Manager-Plus/docs/en/guide/runtime-model.html) | CPA 与 Manager Server 职责 |
| [LibreHardwareMonitor 官方仓库](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | .NET 集成、支持设备与管理员权限 |
| [Lively 官方系统数据](https://github.com/lively-community/lively/wiki/Web-Guide-V-%3A-System-Data) | CPU/GPU/RAM 回调与天气接口状态 |
| [HWiNFO 许可](https://www.hwinfo.com/licenses/) | 非 Pro 共享内存 12 小时限制 |
| [Open-Meteo 文档](https://open-meteo.com/en/docs) | current 字段、模型来源与时区 |

## 源码核对

公开源码在本机 `raw/` 中缓存，已加入 Git 忽略。`sources.json` 记录实际下载文件、来源与 SHA-256。下载基于上游 main/master，不把它们当作用户部署版本；首次接入必须再次确认版本和契约。

### CPAMP

- [快照控制器](https://github.com/seakee/CPA-Manager-Plus/blob/main/apps/manager-server/internal/http/controller/quotasnapshot/handler.go)：`POST /v0/management/quota-snapshots/query`、鉴权、查询/写入的区别。
- [快照服务与响应字段](https://github.com/seakee/CPA-Manager-Plus/blob/main/apps/manager-server/internal/service/quotasnapshot/service.go)：窗口、余量、周期、精度、观测时间、过期标记。
- [快照内部模型](https://github.com/seakee/CPA-Manager-Plus/blob/main/apps/manager-server/internal/model/account_quota_snapshot.go)：账号与窗口身份。
- [窗口生命周期模型](https://github.com/seakee/CPA-Manager-Plus/blob/main/apps/manager-server/internal/model/account_quota_window.go)：周期及可用状态。
- [HTTP 路由](https://github.com/seakee/CPA-Manager-Plus/blob/main/apps/manager-server/internal/http/router/router.go)：快照与巡检路由。
- [Provider 查询逻辑](https://github.com/seakee/CPA-Manager-Plus/blob/main/apps/web/src/utils/quota/providerRequests.ts)：专用配额查询及窗口字段转换。
- [配额类型](https://github.com/seakee/CPA-Manager-Plus/blob/main/apps/web/src/types/quota.ts)：Provider 类型差异。

### LibreHardwareMonitor

- [AMD GPU](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Gpu/AmdGpu.cs)：GPU Core、温度、Hot Spot、显存量与 D3D 信息。
- [DIMM](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Memory/DimmMemory.cs)：DDR4/DDR5 与实际温度、分辨率、阈值字段。
- [SPD 热传感器](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Memory/Sensors/SpdThermalSensor.cs)：温度更新失败可以无值。

## 本机与实例检查

- 通过 CIM 读取系统、CPU、GPU、主板、内存模块；未读取序列号。
- 通过 Windows Screen API 读取当前显示器拓扑。
- 查看 WE 进程与版本文件，不改变配置。
- 从 GPU 驱动注册表读取总显存，核对 WMI AdapterRAM 的截断问题。
- 从用户指定 Hindsight 查询既有 CPAMP 基础设施；对既有实例进行未鉴权的 health/status 只读检查。
- health=200、service=cpa-manager-plus；status=401。真实套餐余量、部署版本和传感器温度尚未验证。

既有仓库的 WE 文档研究与创作者研究也用于背景参考。本轮方案不把此前的全面文档抓取等同于产品运行实测。
