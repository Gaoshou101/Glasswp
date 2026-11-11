using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperBridge;

// Magpie's documented read-only quota contract, internal/provider/quotas.go (MIT).
// The supplied HttpClient must disable automatic redirects, as RemoteSources does.
internal sealed class MagpieQuotaReader(HttpClient client)
{
    private const int MaxBytes = 4 * 1024 * 1024;
    internal static bool ValidateUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/") return false;
        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    internal async Task<QuotaData> Read(string baseUrl, string? apiKey, CancellationToken token)
    {
        if (!ValidateUrl(baseUrl)) return Failure("error", "Magpie 地址无效，请使用 HTTPS 或本机回环地址");
        var endpoint = new Uri(new Uri(baseUrl.Trim()), "/v1/magpie/quotas?kept=1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failure("unauthorized", "Magpie 只读额度访问未获授权，请检查网关密钥及 Quotas 权限");
            if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } actual && actual != endpoint)
                return Failure("error", "Magpie 额度接口发生重定向，已停止读取");
            if (!response.IsSuccessStatusCode) return Failure("offline", "Magpie 额度接口暂时不可用");
            if (response.Content.Headers.ContentLength > MaxBytes) return Failure("error", "Magpie 额度响应超过大小限制");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                if (body.Length + read > MaxBytes) return Failure("error", "Magpie 额度响应超过大小限制");
                body.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            return Parse(json.RootElement, endpoint.GetLeftPart(UriPartial.Authority), DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException or ArgumentException or InvalidOperationException)
        { return Failure("error", "Magpie 额度读取失败，请检查本机服务与地址"); }
    }

    internal static QuotaData Parse(JsonElement root, string source, DateTimeOffset now)
    {
        // HTTP wraps the QuotaReport array in an OpenAI-shaped list; CLI JSON is the array itself.
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var items)) root = items;
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("Expected quota list");
        if (root.GetArrayLength() > 256) throw new JsonException("Too many accounts");
        var accounts = new List<QuotaAccount>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var anyStale = false;
        foreach (var card in root.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object) continue;
            var provider = Text(card, "provider").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(provider, "^[a-z0-9][a-z0-9_-]{0,47}$", RegexOptions.CultureInvariant)) provider = "magpie";
            provider = SafeLabel(provider, "magpie");
            var kind = Text(card, "kind");
            // User is identity only: the hash enters the public DTO, never the email or login.
            var who = Text(card, "user");
            if (who.Length == 0) who = Text(card, "seat");
            if (who.Length == 0) who = Text(card, "name");
            var id = "magpie:" + Hash(JsonSerializer.Serialize(new[] { source, provider, kind, Text(card, "from"), who }));
            if (!identities.Add(id)) continue;
            var alias = SafeLabel(Text(card, "alias"), "");
            if (alias.Length == 0) alias = SafeLabel(Text(card, "seat"), "");
            if (alias.Length == 0) alias = "Magpie · " + provider + " " + (accounts.Count + 1);
            var plan = QuotaAdapter.PlanName(provider, SafeLabel(Text(card, "plan"), ""));
            var observed = Date(card, "readAt") ?? Date(card, "asOf");
            var stale = observed is null || observed > now.AddMinutes(1) || now - observed > TimeSpan.FromMinutes(15) || Text(card, "error").Length > 0;
            anyStale |= stale;
            var windows = new List<QuotaWindow>();
            if (card.TryGetProperty("windows", out var spans) && spans.ValueKind == JsonValueKind.Array)
            {
                if (spans.GetArrayLength() > 128) throw new JsonException("Too many quota windows");
                var windowIds = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var span in spans.EnumerateArray())
                {
                    if (span.ValueKind != JsonValueKind.Object) continue;
                    var rawName = Text(span, "name");
                    var label = SafeLabel(rawName, "额度窗口");
                    var pool = SafeLabel(Text(span, "pool"), "");
                    if (pool.Length > 0) label = pool + " · " + label;
                    var windowHash = Hash(JsonSerializer.Serialize(new[] { rawName, Text(span, "pool") }));
                    windowIds.TryGetValue(windowHash, out var duplicate);
                    windowIds[windowHash] = duplicate + 1;
                    var windowId = id + ":" + windowHash + (duplicate == 0 ? "" : ":" + duplicate);
                    var unlimited = True(span, "unlimited");
                    var textOnly = True(span, "text");
                    var wallet = Number(span, "balance");
                    double? remaining = unlimited || textOnly || wallet is not null ? null : Number(span, "remaining");
                    if (remaining is null && !unlimited && !textOnly && wallet is null && Number(span, "used") is { } used) remaining = 100 - used;
                    if (remaining is not null) remaining = Math.Clamp(remaining.Value, 0, 100);
                    var amount = Number(span, "amount");
                    var limit = Number(span, "limit");
                    var unit = SafeLabel(Text(span, "unit"), "");
                    if (unlimited) label += " · 不限量";
                    var reset = Date(span, "resetsAt");
                    windows.Add(new(windowId, label, remaining, amount, limit > 0 ? limit : null, unit,
                        reset, Duration(rawName), "unknown", reset is null ? "unknown" : "exact", observed, stale,
                        RemainingValue: wallet, DisplayText: unlimited ? "不限量" : textOnly ? SafeLabel(Text(span, "display"), "") : null));
                }
            }
            var balanceText = SafeLabel(Text(card, "balance"), "");
            if (balanceText.Length > 0) windows.Add(new(id + ":balance", "账户余额", null, null, null, "", null, null,
                "non_window", "unknown", observed, stale, DisplayText: balanceText));
            ResetCreditData? resets = null;
            if (card.TryGetProperty("resets", out var credits) && credits.ValueKind == JsonValueKind.Object &&
                Number(credits, "count") is >= 0 and <= int.MaxValue and var available && available == Math.Truncate(available))
                resets = new((int)available, observed, stale);
            accounts.Add(new(id, alias, provider, plan, windows.ToArray(), resets));
        }
        return new(accounts.Count == 0 ? "empty" : anyStale ? "stale" : "ok", now,
            accounts.Count == 0 ? "Magpie 尚无已缓存的套餐信息" : anyStale ? "Magpie 套餐缓存包含过期或未确认的数据" : "已读取 Magpie 套餐缓存", accounts.ToArray());
    }

    private static QuotaData Failure(string status, string message) => new(status, null, message, []);
    private static string Text(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool True(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static double? Number(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static DateTimeOffset? Date(JsonElement node, string name) => DateTimeOffset.TryParse(Text(node, name), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : null;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
    private static long? Duration(string name) => name.Trim().ToLowerInvariant() switch
    { "5h" or "5 hours" or "session" => 18000, "7d" or "7 days" or "week" => 604800, "24h" or "24 hours" or "1d" or "day" => 86400, _ => null };
    private static string SafeLabel(string value, string fallback)
    {
        value = value.Trim();
        if (value.Length is 0 or > 80 || value.Any(char.IsControl) || value.Contains('@') || value.Contains("://", StringComparison.Ordinal) ||
            Regex.IsMatch(value, @"(?i)(?:bearer\s|sk-|eyJ|access[_-]?token|refresh[_-]?token)|[a-zA-Z0-9_\-]{24,}", RegexOptions.CultureInvariant)) return fallback;
        return value;
    }
}
