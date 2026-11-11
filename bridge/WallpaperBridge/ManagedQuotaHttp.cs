using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace WallpaperBridge;

// Never exposes server bodies, credentials or login identities in public errors/DTOs.
internal static class ManagedQuotaHttp
{
    public static JsonElement Field(JsonElement node, params string[] names) {
        foreach (var name in names) if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return value;
        return default;
    }
    public static string Text(JsonElement node, params string[] names) => Field(node, names) is { ValueKind: JsonValueKind.String } value ? value.GetString()!.Trim() : "";
    public static double? Number(JsonElement node, params string[] names) => Field(node, names) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    public static bool Flag(JsonElement node, string name) => Field(node, name).ValueKind == JsonValueKind.True;
    public static string Id(string source, string url, string identity) => source + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url + "\n" + identity))).ToLowerInvariant();
    public static string Label(string text, string fallback = "") => text.Length is > 0 and <= 100 && !text.Any(char.IsControl) ? text : fallback;
    public static DateTimeOffset? Time(JsonElement value) => ClaudeQuotaAdapter.Reset(value);
    public static bool Stale(DateTimeOffset? observed, DateTimeOffset now) => observed is null || observed > now.AddMinutes(1) || now - observed > TimeSpan.FromMinutes(15);
    public static bool Expected(Exception e) => e is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException or ArgumentException;
    public static QuotaData Failure(string name, Exception e) => new(e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } ? "unauthorized" : "offline", null,
        e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } ? name + " 管理密钥或账号读取权限无效" : name + " 读取失败，请检查地址、接口版本与服务状态", []);

    public static async Task<JsonElement> Request(HttpClient client, string baseUrl, string key, string path, object? body, bool sub2api, CancellationToken token)
    {
        // HttpClient's headers-only completion does not time out subsequent body reads.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15)); token = timeout.Token;
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, baseUrl.TrimEnd('/') + path);
        if (sub2api) request.Headers.Add("x-api-key", key);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } final && final != request.RequestUri)
            throw new HttpRequestException("Redirect rejected");
        response.EnsureSuccessStatusCode();
        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new JsonException("Response too large");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream(); var bytes = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(bytes, token)) > 0) {
            if (buffer.Length + count > limit) throw new JsonException("Response too large");
            buffer.Write(bytes, 0, count);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 48 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected object");
        if (sub2api && Number(root, "code") is { } code && code != 0) throw new JsonException("API error envelope");
        var data = Field(root, "data");
        return (data.ValueKind == JsonValueKind.Object ? data : root).Clone();
    }
    public static QuotaAccount Windows(QuotaAccount account, Dictionary<string, object?>[] windows, DateTimeOffset now) {
        var snapshot = JsonSerializer.SerializeToElement(new { items = new[] { new { row_key = account.Id, windows } } });
        var parsed = QuotaAdapter.Parse(snapshot, [new(account.Id, account.Alias, account.Provider, "", "", "")], now).Single();
        return parsed with { Plan = Label(parsed.Plan, account.Plan), Windows = parsed.Windows.Select(w => w with { Stale = w.Stale || Stale(w.ObservedAt, now) }).ToArray() };
    }
    public static QuotaData Result(string name, QuotaAccount[] accounts, bool failed, DateTimeOffset now) {
        var stale = failed || accounts.Any(a => a.Windows.Length == 0 || a.Windows.Any(w => w.Stale));
        return new(accounts.Length == 0 ? "empty" : stale ? "stale" : "ok", now,
            accounts.Length == 0 ? name + " 未发现可读取的账号" : stale ? name + " 部分账号缺少有效余量或仅有过期证据" : name + " 套餐余量已更新", accounts);
    }
}
