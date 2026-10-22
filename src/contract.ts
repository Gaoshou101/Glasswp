export type DataStatus = 'ok' | 'stale' | 'unconfigured' | 'unauthorized' | 'unsupported' | 'offline' | 'empty' | 'error';
export interface Metric { value: number | null; unit: string; source: string; sensorId: string | null }
export interface HardwareDevice {
  name: string; load: Metric; temperature: Metric; usedBytes: number | null; totalBytes: number | null;
}
export interface HardwareData {
  status: DataStatus; observedAt: string | null; cpu: HardwareDevice; gpu: HardwareDevice;
  vram: HardwareDevice; ram: HardwareDevice; message: string;
}
export interface QuotaWindow {
  id: string; label: string; remainingPercent: number | null; usedValue: number | null;
  limitValue: number | null; quotaUnit: string; resetAt: string | null; durationSeconds: number | null;
  mode: string; accuracy: string; observedAt: string | null; stale: boolean;
}
export interface ResetCreditData { available: number | null; observedAt: string | null; stale: boolean }
export interface QuotaAccount { id: string; alias: string; provider: string; plan: string; windows: QuotaWindow[]; resetCredits?: ResetCreditData | null }
export interface RequestActivity { time: string; model: string; success: boolean | null; statusCode: number | null; tokens: number | null; inputTokens: number | null; outputTokens: number | null; cachedTokens: number | null; durationMs: number | null; ttftMs?: number | null }
export interface AccountActivity { accountId: string; status: DataStatus; fetchedAt: string | null; from: string; to: string; requests: number | null; successRate: number | null; tokens: number | null; estimatedCost: number | null; lastActivity: string | null; recent: RequestActivity[]; hasMore: boolean; message: string; nextBeforeMs?: number | null; nextBeforeId?: number | null; totalCount?: number | null }
export interface QuotaData { status: DataStatus; fetchedAt: string | null; message: string; accounts: QuotaAccount[] }
export interface WeatherHour { time: string; temperature: number | null; precipitationProbability: number | null; precipitationMm: number | null; code: number | null }
export interface WeatherData {
  status: DataStatus; fetchedAt: string | null; observedAt: string | null; city: string; temperature: number | null;
  low: number | null; high: number | null; code: number | null; isDay: boolean; message: string;
  hourly?: WeatherHour[] | null; timezone?: string | null;
}
export interface Snapshot { schemaVersion: 1; generatedAt: string; hardware: HardwareData; quota: QuotaData; weather: WeatherData; revisions?: { epoch: string; hardware: number; quota: number; weather: number } }
export interface City { id: string; name: string; latitude: number; longitude: number; timezone: string }
