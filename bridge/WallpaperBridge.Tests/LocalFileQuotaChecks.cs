using System.Net;
using System.Text;
using System.Text.Json;
using WallpaperBridge;

internal static class LocalFileQuotaChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var times = JsonSerializer.SerializeToElement(new { reset_at = "2026-11-10 20:00:00", explicit_date = "2026-11-10T20:00:00Z", epoch = 1794312000 });
        check(LocalFileQuotaReader.AtomDate(times, "reset_at") == DateTimeOffset.Parse("2026-11-10T12:00:00Z") && LocalFileQuotaReader.AtomDate(times, "explicit_date") == DateTimeOffset.Parse("2026-11-10T20:00:00Z"), "AtomCode timezone-less reset uses Beijing time while explicit zone is preserved");
        var root = Path.Combine(Path.GetTempPath(), "wallpaper-native-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            void FileAt(string relative, string content) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
            var jwt = "eyJ0eXAiOiJKV1QifQ." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"username\":\"PRIVATE-USER\",\"exp\":4102444800}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
            using var handler = new Handler(); using var client = new HttpClient(handler);
            var reader = new LocalFileQuotaReader(client, root, () => "fixture-device-uuid");
            check((await reader.Read(default)).Status == "unconfigured" && handler.Requests == 0, "native files: absent logins never send requests");
            FileAt(".commandcode/auth.json", "{\"apiKey\":\"SECRET-COMMAND\",\"userId\":\"PRIVATE-USER\"}");
            FileAt(".grok/auth.json", "{\"main\":{\"key\":\"SECRET-GROK\",\"expires_at\":4102444800}}");
            FileAt("AppData/Roaming/devin/credentials.toml", "windsurf_api_key = 'SECRET-DEVIN'\napi_server_url = 'https://evil.test'\n");
            FileAt(".meituan-catpaw/auth.json", "{\"auth\":{\"accessToken\":\"SECRET-CATPAW\"},\"account\":{\"uid\":\"PRIVATE-USER\"}}");
            FileAt(".office-raccoon/agent/config/auth.json", "{\"access_token\":\"Bearer SECRET-RACCOON\"}");
            FileAt(".atomcode/auth.toml", "access_token = 'SECRET-ATOM'\n[user]\nid = 'PRIVATE-USER'\n");
            FileAt(".codar/config.json", JsonSerializer.Serialize(new { provider = new { speedai = new { token = jwt } } }));
            FileAt(".deepai-code/userdata/secrets/deepai-account.bin", "{\"apiKey\":\"SECRET-DEEPAI\",\"username\":\"PRIVATE-USER\"}");
            var result = await reader.Read(default);
            check(result.Status == "ok" && result.Accounts.Length == 8, "native files: all eight readers discover and load native accounts");
            QuotaAccount Account(string id) => result.Accounts.Single(a => a.Provider == id);
            check(Account("commandcode-plan").Plan == "Pro" && Account("commandcode-plan").Windows.Single(w => w.Id == "fiveHour").RemainingPercent == 75, "Command Code reads plan and limit windows");
            check(Account("commandcode-plan").Windows.Single(w => w.Id == "credits").QuotaUnit == "credits" && Account("commandcode-plan").Windows.Single(w => w.Id == "extra").RemainingValue == 3, "Command Code credit plan and purchased dollars remain distinct");
            check(Account("grok").Windows[0].RemainingPercent == 60 && Account("grok").Plan == "", "Grok reads usage without inventing a plan");
            check(Account("devin").Windows[0].RemainingPercent == 0, "Devin protobuf omitted quota is zero when reset and quota billing exist");
            check(Account("catpaw").Windows.Single().RemainingValue == 0 && Account("catpaw").Windows.Single().RemainingPercent is null, "CatPaw preserves zero wallet without fake total");
            check(Account("raccoon").Windows.Single().RemainingValue == 123, "Raccoon native bearer balance is preserved");
            check(Account("atomcode").Windows.Single().RemainingPercent == 75, "AtomCode keeps call window counts");
            check(Account("codar").Windows.Length == 2 && Account("codar").Windows[1].RemainingValue == 27, "Codar independently reads general and agent balances");
            check(Account("deepaicode").Windows[0].RemainingPercent == 80 && Account("deepaicode").Plan == "Pro", "DeepAI reads Theos allowance and subscription summary");
            var serialized = JsonSerializer.Serialize(result);
            check(!serialized.Contains("SECRET-") && !serialized.Contains("PRIVATE-USER") && !serialized.Contains(jwt), "native files: public data contains no raw login or identity");
            check(handler.DeepaiHeaders && handler.CatpawHeader && handler.DevinMetadata && handler.NoUnsafeEndpoints, "native files: provider authentication and fixed read endpoints match contract");
            handler.CancelCommand = true;
            var canceled = (await reader.Read(default)).Accounts.Single(a => a.Provider == "commandcode-plan");
            check(canceled.Plan == "No plan" && canceled.Windows.All(w => w.Id != "credits") && canceled.Windows.Any(w => w.Id == "extra" && w.RemainingValue == 3), "Command Code cancelled subscription retains wallet without paid plan or invented monthly limit");
            handler.CancelCommand = false; handler.FailCommandCredits = true;
            var partial = (await reader.Read(default)).Accounts.Single(a => a.Provider == "commandcode-plan");
            check(partial.Plan == "Pro" && partial.Windows.Length == 0, "Command Code quota outage preserves confirmed plan without fake allowance");
            handler.FailCommandCredits = false;
            var deepaiReads = handler.DeepaiReads; await reader.Read(default);
            check(handler.DeepaiReads == deepaiReads, "DeepAI prevents manual refresh from repeating daily allowance requests within ten minutes");
            handler.MalformedOptional = true;
            var optionalFailed = await new LocalFileQuotaReader(client, root, () => "fixture-device-uuid").Read(default);
            check(optionalFailed.Accounts.Single(a => a.Provider == "deepaicode").Windows[0].RemainingPercent == 80 && optionalFailed.Accounts.Single(a => a.Provider == "raccoon").Windows.Single().RemainingValue == 123, "optional plan metadata failure cannot discard measured DeepAI or Raccoon balances");
            handler.MalformedOptional = false;
            handler.FailGrok = true; var degraded = await reader.Read(default);
            check(degraded.Status == "stale" && degraded.Accounts.Length == 7 && !JsonSerializer.Serialize(degraded).Contains("UPSTREAM-SECRET"), "native files: provider failure preserves others and sanitizes upstream errors");
            handler.SwitchCatpaw = () => FileAt(".meituan-catpaw/auth.json", "{\"auth\":{\"accessToken\":\"CHANGED\"}}");
            var switched = await reader.Read(default);
            check(switched.Accounts.All(a => a.Provider != "catpaw"), "native files: login switch discards an in-flight old account response");
            FileAt(".grok/auth.json", "{\"main\":{\"key\":\"SECRET-GROK\",\"expires_at\":1}}");
            var grokReads = handler.GrokReads; await reader.Read(default);
            check(handler.GrokReads == grokReads, "native files: expired native credential is not sent or refreshed");
        } finally { Directory.Delete(root, true); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests, DeepaiReads, GrokReads;
        public bool FailGrok, CancelCommand, FailCommandCredits, MalformedOptional, DeepaiHeaders, CatpawHeader, DevinMetadata, NoUnsafeEndpoints = true;
        public Action? SwitchCatpaw;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; var url = request.RequestUri!; string payload;
            switch (url.Host + url.AbsolutePath) {
                case "api.commandcode.ai/alpha/billing/credits":
                    if (FailCommandCredits) return new(HttpStatusCode.ServiceUnavailable);
                    payload = """{"credits":{"planId":"individual-pro","monthlyCredits":20,"purchasedCredits":3},"windowLimits":{"fiveHour":{"used":25,"cap":100}}}"""; break;
                case "api.commandcode.ai/alpha/billing/subscriptions": payload = """{"success":true,"data":{"planId":"individual-pro","status":"ACTIVE"}}""".Replace("ACTIVE", CancelCommand ? "canceled" : "active"); break;
                case "cli-chat-proxy.grok.com/v1/billing":
                    GrokReads++; if (FailGrok) return new(HttpStatusCode.Forbidden) { Content = new StringContent("UPSTREAM-SECRET") };
                    payload = """{"config":{"creditUsagePercent":40,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY"}}}"""; break;
                case "server.codeium.com/exa.seat_management_pb.SeatManagementService/GetUserStatus":
                    DevinMetadata = request.Method == HttpMethod.Post && request.Headers.Contains("Connect-Protocol-Version") && (await request.Content!.ReadAsStringAsync(token)).Contains("SECRET-DEVIN");
                    payload = """{"userStatus":{"planStatus":{"planInfo":{"planName":"Pro","billingStrategy":"BILLING_STRATEGY_QUOTA"},"dailyQuotaResetAtUnix":4102444800}}}"""; break;
                case "catx.nocode.cn/api/gateway/credit/balance":
                    CatpawHeader = request.Headers.Contains("X-Auth-Token") && request.Headers.Authorization is null;
                    SwitchCatpaw?.Invoke(); payload = """{"code":0,"data":{"availableCredits":0,"userPlan":{"planName":"Pro"}}}"""; break;
                case "xiaohuanxiong.com/api/web/points/v1/balance": payload = """{"code":0,"data":{"available_points":123}}"""; break;
                case "xiaohuanxiong.com/api/web/auth/v1/entitlement_info": payload = MalformedOptional ? "bad json" : """{"data":{"office":{"pro_enable":true,"active_plan":{"type":"Pro"}}}}"""; break;
                case "api.gitcode.com/api/v5/coding-plan/status-v2": payload = """{"plan":{"plan_name":"Pro"},"rate_limit_windows":[{"calls_used":25,"call_limit":100,"window_size_seconds":18000}]}"""; break;
                case "api.kuaipaper.com/v1/gettoken": payload = """{"code":200,"token":0}"""; break;
                case "api.kuaipaper.com/v1/agent/balance": payload = """{"code":200,"data":{"agent_balance":27}}"""; break;
                case "api.deepai.org/deepai_code/theos":
                    DeepaiReads++; DeepaiHeaders = request.Headers.Contains("Api-Key") && request.Headers.Contains("X-DeepAI-Code-Machine") && request.Headers.Authorization is null;
                    payload = """{"enabled":true,"balance":"80","granted":"100"}"""; break;
                case "api.deepai.org/get_usage_summary": payload = MalformedOptional ? "bad json" : """{"plan":"Pro","credits_remaining":42}"""; break;
                default: NoUnsafeEndpoints = false; throw new InvalidOperationException("unexpected quota endpoint");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }
}
