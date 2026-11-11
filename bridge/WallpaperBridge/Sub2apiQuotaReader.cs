using System.Net;
using System.Text.Json;
using static WallpaperBridge.ManagedQuotaHttp;

namespace WallpaperBridge;

internal sealed class Sub2apiQuotaReader(HttpClient client)
{
    public async Task<QuotaData> Read(AiSourceSettings settings, CancellationToken token)
    {
        if (settings.Sub2apiUrl.Length == 0 || string.IsNullOrEmpty(settings.Sub2apiKey)) return new("unconfigured", null, "请保存 Sub2API 地址与管理员 API Key", []);
        if (!MagpieQuotaReader.ValidateUrl(settings.Sub2apiUrl)) return new("error", null, "Sub2API 地址无效", []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        Task<JsonElement> Get(string path) => Request(client, settings.Sub2apiUrl, settings.Sub2apiKey, "/api/v1/admin" + path, null, true, ct);
        try {
            var files = new List<JsonElement>(); var ids = new HashSet<long>();
            for (var page = 1; ; page++) {
                var list = await Get($"/accounts?page={page}&page_size=100&lite=true");
                var items = Field(list, "items");
                if (items.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid account page");
                foreach (var item in items.EnumerateArray()) {
                    var rawId = Field(item, "id");
                    if (rawId.ValueKind != JsonValueKind.Number || !rawId.TryGetInt64(out var id) || id <= 0 || !ids.Add(id)) throw new JsonException("Invalid or repeated account id");
                    files.Add(item);
                }
                if (files.Count > 200) throw new JsonException("Too many accounts");
                var total = Number(list, "total");
                if (total is not null && files.Count >= total || total is null && items.GetArrayLength() < 100) break;
                if (items.GetArrayLength() == 0 || page >= 2) throw new JsonException("Incomplete account pagination");
            }
            using var slots = new SemaphoreSlim(4); var failed = 0;
            var accounts = await Task.WhenAll(files.Select(async (file, index) => {
                var platform = Text(file, "platform");
                var provider = platform switch { "openai" => "codex", "anthropic" => "claude", _ => platform };
                if (provider.Length == 0 || Text(file, "status") == "disabled") return null;
                var accountId = Field(file, "id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var credentials = Field(file, "credentials"); var extra = Field(file, "extra");
                var plan = Label(Text(credentials, "plan_type", "subscription_type"));
                if (plan.Length == 0) plan = Label(Text(extra, "plan_type", "subscription_type"));
                var account = new QuotaAccount(Id("sub2api", settings.Sub2apiUrl, accountId),
                    Label(Text(file, "name"), "Sub2API · " + Label(provider, "账号") + " " + (index + 1)), provider, QuotaAdapter.PlanName(provider, plan), []);
                // Setup-token estimates and site-defined API-key spending limits are not upstream subscription quota.
                if (Text(file, "type") != "oauth" || provider is not ("codex" or "claude" or "antigravity")) return account;
                var entered = false;
                try {
                    await slots.WaitAsync(ct); entered = true;
                    var now = DateTimeOffset.UtcNow;
                    if (provider == "codex") {
                        // Do not use /accounts/:id/usage: that path may synthesize a /responses request.
                        var usage = await Get("/openai/accounts/" + accountId + "/quota");
                        var observed = Time(Field(usage, "fetched_at")) ?? now;
                        return ManagedQuotaHttp.Windows(account, UpstreamQuotaAdapter.Windows(usage, observed), now);
                    }
                    return ParseUsage(await Get("/accounts/" + accountId + "/usage?source=active"), account, now);
                } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (Expected(e)) { Interlocked.Increment(ref failed); return account; }
                finally { if (entered) slots.Release(); }
            }));
            return Result("Sub2API", accounts.OfType<QuotaAccount>().ToArray(), failed > 0, DateTimeOffset.UtcNow);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (Expected(e)) { return Failure("Sub2API", e); }
    }

    internal static QuotaAccount ParseUsage(JsonElement usage, QuotaAccount account, DateTimeOffset now)
    {
        if (Text(usage, "error", "error_code").Length > 0 || Flag(usage, "is_forbidden") || Flag(usage, "needs_reauth")) return account;
        var observed = Time(Field(usage, "updated_at"));
        var windows = new List<QuotaWindow>();
        void Add(JsonElement node, string id, string label, long? duration) {
            var used = Number(node, "utilization");
            if (used is null or < 0) return;
            if (windows.Count >= 128) throw new JsonException("Too many quota windows");
            var reset = Time(Field(node, "resets_at", "reset_time"));
            windows.Add(new(id, Label(label, "额度窗口"), Math.Clamp(100 - used.Value, 0, 100), null, null, "", reset, duration, "fixed",
                reset is null ? "unknown" : "exact", observed, Stale(observed, now)));
        }
        foreach (var (key, label, duration) in new[] { ("five_hour", "5 小时窗口", 18000L), ("seven_day", "周窗口", 604800L),
            ("seven_day_sonnet", "周窗口 · Sonnet", 604800L), ("seven_day_fable", "周窗口 · Fable", 604800L), ("thirty_day", "30 天窗口", 2592000L) })
            Add(Field(usage, key), key, label, duration);
        var groups = Field(usage, "antigravity_quota_groups");
        if (groups.ValueKind == JsonValueKind.Array) foreach (var group in groups.EnumerateArray()) {
            var buckets = Field(group, "windows"); if (buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var bucket in buckets.EnumerateArray()) {
                var key = Text(bucket, "window"); var name = Label(Text(group, "name", "key"));
                Add(bucket, "group-" + windows.Count, name + " · " + key, key switch { "5h" => 18000L, "7d" => 604800L, _ => null });
            }
        }
        // Group windows and per-model windows are alternative inventories, not additive buckets.
        if (windows.Count == 0 && Field(usage, "antigravity_quota") is { ValueKind: JsonValueKind.Object } models)
            foreach (var model in models.EnumerateObject()) Add(model.Value, "model-" + windows.Count, model.Name, null);
        var plan = Label(Text(usage, "subscription_tier_raw", "subscription_tier"), account.Plan);
        return account with { Plan = QuotaAdapter.PlanName(account.Provider, plan), Windows = windows.ToArray() };
    }
}
