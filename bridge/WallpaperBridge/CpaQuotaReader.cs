using System.Net;
using System.Text.Json;
using static WallpaperBridge.ManagedQuotaHttp;

namespace WallpaperBridge;

internal sealed class CpaQuotaReader(HttpClient client)
{
    public async Task<QuotaData> Read(AiSourceSettings settings, CancellationToken token)
    {
        if (settings.CpaUrl.Length == 0 || string.IsNullOrEmpty(settings.CpaKey)) return new("unconfigured", null, "请保存 CPA 地址与管理密钥", []);
        if (!MagpieQuotaReader.ValidateUrl(settings.CpaUrl)) return new("error", null, "CPA 地址无效", []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = timeout.Token; var prefix = "/" + settings.CpaApiVersion + "/management";
        Task<JsonElement> Get(string path, object? body = null) => Request(client, settings.CpaUrl, settings.CpaKey, prefix + path, body, false, ct);
        try {
            var list = await Get(settings.CpaApiVersion == "v8" ? "/credentials" : "/auth-files");
            var files = Field(list, "files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 200) throw new JsonException("Invalid account list");
            using var slots = new SemaphoreSlim(4);
            var failed = 0;
            var accounts = await Task.WhenAll(files.EnumerateArray().Select(async (file, index) => {
                var provider = Text(file, "provider", "type").ToLowerInvariant();
                var authIndex = file.ValueKind == JsonValueKind.Object ? CachedHeaderAdapter.StringOrNumber(file, "auth_index") : "";
                var name = Text(file, "name");
                var plugin = Text(file, "quota_plugin_id");
                if (provider.Length == 0 || authIndex.Length == 0 || Flag(file, "disabled")) return null;
                var id = Id("cpa", settings.CpaUrl, provider + ":" + name + ":" + authIndex);
                var alias = Label(Text(file, "label"), "CPA · " + Label(provider, "账号") + " " + (index + 1));
                var claims = Field(file, "id_token");
                var plan = Label(Text(claims, "plan_type"));
                var account = new QuotaAccount(id, alias, provider, QuotaAdapter.PlanName(provider, plan), []);
                var entered = false;
                try {
                    await slots.WaitAsync(ct); entered = true;
                    if (plugin.Length > 0) {
                        try { return ParsePlugin(await Get("/plugins/" + Uri.EscapeDataString(plugin) + "/quota?auth_index=" + Uri.EscapeDataString(authIndex)), account, DateTimeOffset.UtcNow); }
                        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented) { }
                    }
                    var selection = CachedHeaderAdapter.Identity(file, new(id, alias, provider, name, authIndex, ""));
                    async Task<JsonElement> Usage(object body) {
                        var wrapper = await Get(settings.CpaApiVersion == "v8" ? "/requests/api-call" : "/api-call", body);
                        var status = Number(wrapper, "status_code", "statusCode");
                        if (status is not (>= 200 and < 300)) throw new HttpRequestException("Provider query failed", null, status is >= 100 and <= 599 ? (HttpStatusCode)(int)status : null);
                        var content = Field(wrapper, "body");
                        if (content.ValueKind == JsonValueKind.Object) return content;
                        if (content.ValueKind != JsonValueKind.String) throw new JsonException("Missing provider body");
                        using var doc = JsonDocument.Parse(content.GetString()!, new JsonDocumentOptions { MaxDepth = 48 }); return doc.RootElement.Clone();
                    }
                    var observed = DateTimeOffset.UtcNow;
                    Dictionary<string, object?>[] windows;
                    if (provider == "codex") {
                        if (selection.AccountId.Length == 0) throw new JsonException("Missing workspace identity");
                        windows = UpstreamQuotaAdapter.Windows(await Usage(UpstreamQuotaAdapter.Request(selection)), observed);
                    } else if (provider == "claude") {
                        var usage = await Usage(ClaudeQuotaAdapter.Request(selection, ClaudeQuotaAdapter.UsageUrl));
                        try { plan = ClaudeQuotaAdapter.ParsePlan(await Usage(ClaudeQuotaAdapter.Request(selection, ClaudeQuotaAdapter.ProfileUrl))) ?? plan; }
                        catch (Exception e) when (Expected(e) && !ct.IsCancellationRequested) { }
                        windows = ClaudeQuotaAdapter.Windows(usage, observed, plan);
                    } else if (provider == "antigravity") {
                        var project = AntigravityQuotaAdapter.Project(file); windows = [];
                        foreach (var url in AntigravityQuotaAdapter.SummaryUrls.Concat(AntigravityQuotaAdapter.ModelUrls)) {
                            try {
                                var usage = await Usage(AntigravityQuotaAdapter.Request(selection, project, url));
                                windows = url.Contains("retrieveUserQuotaSummary", StringComparison.Ordinal) ? AntigravityQuotaAdapter.Windows(usage, observed) : AntigravityQuotaAdapter.ModelWindows(usage, observed);
                                break;
                            } catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) { throw; }
                            catch (Exception e) when (Expected(e) && !ct.IsCancellationRequested) { }
                        }
                        if (windows.Length == 0) throw new JsonException("Missing quota");
                        try {
                            plan = AntigravitySubscriptionReader.ParsePlan(await Usage(new {
                                authIndex = selection.AuthIndex, method = "POST", url = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
                                header = new Dictionary<string, string> { ["Authorization"] = "Bearer $TOKEN$", ["Content-Type"] = "application/json", ["User-Agent"] = AntigravityQuotaAdapter.UserAgent },
                                data = JsonSerializer.Serialize(new { metadata = new { ideType = "ANTIGRAVITY" } })
                            })) ?? plan;
                        } catch (Exception e) when (Expected(e) && !ct.IsCancellationRequested) { }
                    } else return account;
                    var parsed = ManagedQuotaHttp.Windows(account, windows, observed);
                    return provider == "antigravity" && plan.Length > 0 ? parsed with { Plan = Label(plan) } : parsed;
                } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (Expected(e)) { Interlocked.Increment(ref failed); return account; }
                finally { if (entered) slots.Release(); }
            }));
            return Result("CPA", accounts.OfType<QuotaAccount>().ToArray(), failed > 0, DateTimeOffset.UtcNow);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (Expected(e)) { return Failure("CPA", e); }
    }

    internal static QuotaAccount ParsePlugin(JsonElement root, QuotaAccount account, DateTimeOffset now)
    {
        var subscription = Field(root, "subscription");
        var plan = Label(Text(subscription, "plan", "tierName", "tier_name"), account.Plan);
        var windows = new List<QuotaWindow>(); var groups = Field(root, "groups");
        if (groups.ValueKind == JsonValueKind.Array) foreach (var group in groups.EnumerateArray()) {
            var buckets = Field(group, "buckets"); if (buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var bucket in buckets.EnumerateArray()) {
                if (windows.Count >= 128) throw new JsonException("Too many quota windows");
                var remaining = Number(bucket, "remainingFraction", "remaining_fraction");
                if (remaining is not (>= 0 and <= 1)) continue;
                var key = Text(bucket, "window"); var groupName = Label(Text(group, "displayName", "display_name"));
                var duration = key switch { "5h" => 18000L, "7d" or "weekly" => 604800L, "1d" or "daily" => 86400L, _ => (long?)null };
                var reset = Time(Field(bucket, "resetTime", "reset_time"));
                windows.Add(new("plugin-" + windows.Count, Label(groupName + " · " + key, "额度窗口"), remaining * 100, null, null, "", reset, duration, "fixed", reset is null ? "unknown" : "exact", now, false));
            }
        }
        return account with { Plan = QuotaAdapter.PlanName(account.Provider, plan), Windows = windows.ToArray() };
    }
}
