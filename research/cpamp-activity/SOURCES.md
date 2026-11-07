# 请求活动时间范围与接口口径

核对版本：CPAMP v1.13.1，2026-10-15；当前已部署接口另通过配置中的地址只读核对。

| 内容 | 官方源码 |
| --- | --- |
| 全部七种范围与自然日边界 | [range.ts](https://github.com/seakee/CPA-Manager-Plus/blob/v1.13.1/apps/web/src/features/monitoring/model/range.ts) |
| 全部历史下限归一为 Unix 毫秒 1 | [useMonitoringData.ts](https://github.com/seakee/CPA-Manager-Plus/blob/v1.13.1/apps/web/src/features/monitoring/hooks/useMonitoringData.ts) |
| 浏览器时区与请求体构建 | [useMonitoringAnalytics.ts](https://github.com/seakee/CPA-Manager-Plus/blob/v1.13.1/apps/web/src/features/monitoring/hooks/useMonitoringAnalytics.ts) |
| 时间范围、include、汇总、明细与分页契约 | [usageService.ts](https://github.com/seakee/CPA-Manager-Plus/blob/v1.13.1/apps/web/src/services/api/usageService.ts) |
| `from_ms > 0`、`to_ms > from_ms` 参数校验 | [handler.go](https://github.com/seakee/CPA-Manager-Plus/blob/v1.13.1/apps/manager-server/internal/http/controller/monitoring/handler.go) |

本功能对应账号请求活动页：`monitoring/analytics` 的 compact summary 与 events page，账号物理文件及平台过滤固定。总览使用 total_calls/success_rate/total_tokens/total_cost；明细使用 timestamp_ms/analytics_model/failed/fail_status_code/input_tokens/output_tokens/cache_read_tokens/total_tokens/ttft_ms/latency_ms；分页使用 next_before_ms/next_before_id/total_count/has_more。页面保留当前 20 行，翻页固定起止。

时间范围全部来自上述 CPAMP 版本，默认 7 天。私有凭证不放入研究文件；原始身份、哈希、会话与访问令牌字段不映射到展示接口。
