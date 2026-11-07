using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WallpaperBridge;

// Subscription metadata is independent of quota windows. Never turn a tier lookup
// failure into missing/stale quota, or expose the management credential to the UI.
public sealed class AntigravitySubscriptionReader(HttpClient client, Func<DateTimeOffset>? clock = null)
{
    private static readonly string[] Urls = [
        "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
        "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
    ];
    private sealed record Entry(string? Plan, DateTimeOffset? ObservedAt, DateTimeOffset NextAttempt);
    private readonly Dictionary<string, Entry> cache = new();
    private string source = "";
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public static string? ParsePlan(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        JsonElement Tier(string camel, string snake) => payload.TryGetProperty(camel, out var value) || payload.TryGetProperty(snake, out value) ? value : default;
        var paid = Tier("paidTier", "paid_tier");
        var current = Tier("currentTier", "current_tier");
        var tier = paid.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(QuotaAdapter.Text(paid, "id")) ? paid : current;
        if (tier.ValueKind != JsonValueKind.Object) return null;
        var name = QuotaAdapter.Text(tier, "name").Trim();
        if (name.Length > 0 && name.Length <= 80 && !name.Any(char.IsControl)) return name;
        return QuotaAdapter.Text(tier, "id").Trim() switch {
            "g1-pro-tier" => "Google AI Pro", "g1-ultra-tier" => "Google AI Ultra",
            "g1-ultra-lite-tier" => "Google AI Ultra Lite", "free-tier" or "standard-tier" => "antigravity", _ => null
        };
    }

    public async Task<string?> Read(BridgeConfig config, AccountSelection account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (account.Provider != "antigravity" || string.IsNullOrWhiteSpace(account.AuthIndex)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.CpampUrl + "\n" + config.ManagementKey)));
        if (source != key) { cache.Clear(); source = key; }
        var identity = account.Id + ":" + account.AuthIndex;
        cache.TryGetValue(identity, out var previous);
        string? CachedPlan() => previous?.ObservedAt is { } at && Now - at < TimeSpan.FromDays(1) ? previous.Plan : null;
        if (previous is not null && Now < previous.NextAttempt) return CachedPlan();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        foreach (var url in Urls)
        {
            try {
                using var request = new HttpRequestMessage(HttpMethod.Post, config.CpampUrl.TrimEnd('/') + "/v0/management/api-call");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ManagementKey);
                request.Content = JsonContent.Create(new {
                    authIndex = account.AuthIndex, method = "POST", url,
                    header = new Dictionary<string, string> { ["Authorization"] = "Bearer $TOKEN$", ["Content-Type"] = "application/json", ["User-Agent"] = AntigravityQuotaAdapter.UserAgent },
                    data = JsonSerializer.Serialize(new { metadata = new { ideType = "ANTIGRAVITY" } })
                });
                using var response = await client.SendAsync(request, budget.Token);
                response.EnsureSuccessStatusCode();
                using var wrapper = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token));
                var root = QuotaAdapter.Unwrap(wrapper.RootElement);
                var status = QuotaAdapter.Number(root, "status_code") ?? QuotaAdapter.Number(root, "statusCode");
                if (status is not (>= 200 and < 300)) throw new HttpRequestException(null, null, status is >= 100 and <= 599 ? (HttpStatusCode)(int)status : null);
                var body = root.GetProperty("body");
                using var payload = JsonDocument.Parse(body.ValueKind == JsonValueKind.String ? body.GetString()! : body.GetRawText());
                var plan = ParsePlan(payload.RootElement);
                cache[identity] = new(plan, Now, Now.AddHours(1));
                return plan;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) { break; }
            catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException) {
                if (budget.IsCancellationRequested) break;
            }
        }
        cache[identity] = new(CachedPlan(), previous?.ObservedAt, Now.AddMinutes(10));
        return CachedPlan();
    }
}
