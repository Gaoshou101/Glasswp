using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static WallpaperBridge.LocalKiroQuotaReader;

namespace WallpaperBridge;

// Kimi owns this login and its rotation. Only the current desktop user's membership is queried.
internal sealed class LocalKimiWorkQuotaReader(HttpClient client, string? userHome = null)
{
    private const string MembershipUrl = "https://www.kimi.com/apiv2/kimi.gateway.membership.v2.MembershipService/GetSubscriptionStats";
    private sealed record Login(string UserId, string Token, string Fingerprint);
    private string Home => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public async Task<QuotaData> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var login = await ReadLogin(token);
            if (login is null) return new("unconfigured", null, "Kimi Work：未发现当前桌面版登录，请先在 Kimi 中进入 Work", []);
            if (TokenExpired(login.Token)) return new("unauthorized", null, "Kimi Work：会员登录已过期，请打开 Kimi 桌面版更新登录", []);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Post, MembershipUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
            request.Headers.Accept.Add(new("application/json"));
            request.Headers.UserAgent.ParseAdd("kimi-code-cli/0.36.0");
            request.Headers.Add("x-msh-platform", "windows");
            request.Headers.Add("x-msh-version", "3.2.20");
            request.Headers.Add("R-Timezone", "Asia/Shanghai");
            request.Headers.Add("Connect-Protocol-Version", "1");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new("unauthorized", null, "Kimi Work：会员登录不可用，打开 Kimi 桌面版以恢复额度读取", []);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri)
                return new("offline", null, "Kimi Work：会员额度服务暂不可用", []);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var result = await Json(stream, timeout.Token);
            var observed = DateTimeOffset.UtcNow;
            var windows = Parse(result, observed);
            if (windows.Length == 0) return new("offline", null, "Kimi Work：会员回复未包含共享额度", []);
            var after = await ReadLogin(timeout.Token);
            if (after?.Fingerprint != login.Fingerprint) return new("unconfigured", null, "Kimi Work：登录已切换，等待重新读取", []);
            return new("ok", observed, "Kimi Work：已读取桌面版当前账号的会员共享额度",
                [new("local:kimi-work:" + Hash(login.UserId), "Kimi Work 本机", "kimi-work", "月度共享额度", windows)]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or OperationCanceledException or ArgumentException or FormatException)
        { return new("offline", null, "Kimi Work：本地登录或会员额度暂不可读取", []); }
    }

    private async Task<Login?> ReadLogin(CancellationToken token)
    {
        var appData = userHome is null ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Combine(Home, "AppData", "Roaming");
        var userData = Path.Combine(appData, "kimi-desktop");
        var share = Path.Combine(userData, "daimon-share");
        try
        {
            var pointer = await ReadJson(Path.Combine(userData, "daimon-storage.json"), token);
            var location = Text(pointer, "shareDir");
            if (Path.IsPathFullyQualified(location)) share = Path.GetFullPath(location);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        JsonElement config;
        try { config = await ReadJson(Path.Combine(share, "daimon", "config.json"), token); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var credentials = Field(config, "credentials");
        var work = Field(credentials, "kimiCode"); var web = Field(credentials, "kimiWeb");
        var workToken = Text(work, "apiKey"); var access = Text(web, "accessToken"); var uid = Text(web, "userId");
        if (workToken.Length == 0 || access.Length == 0 || uid.Length == 0 || Text(work, "baseUrl").TrimEnd('/') != "https://agent-gw.kimi.com/coding/v1") return null;
        return new(uid, access, Hash(string.Join('\0', uid, access, workToken)));
    }

    internal static QuotaWindow[] Parse(JsonElement data, DateTimeOffset observed)
    {
        var balance = Field(data, "subscriptionBalance");
        if (balance.ValueKind != JsonValueKind.Object) balance = Field(data, "subscription_balance");
        if (balance.ValueKind != JsonValueKind.Object) return [];
        // Connect/protobuf JSON omits its zero default, so an existing balance without ratio means zero used.
        var camel = Field(balance, "amountUsedRatio"); var snake = Field(balance, "amount_used_ratio");
        var used = camel.ValueKind != JsonValueKind.Undefined ? Num(balance, "amountUsedRatio") : Num(balance, "amount_used_ratio");
        if (used is null && (camel.ValueKind != JsonValueKind.Undefined || snake.ValueKind != JsonValueKind.Undefined)) return [];
        used ??= 0;
        var code = Num(balance, "kimiCodeUsedRatio") ?? Num(balance, "kimi_code_used_ratio");
        var codeText = code.HasValue ? "Code 已用 " + Math.Clamp(100 * code.Value, 0, 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%（占总额）" : null;
        return [new("kimi-work:monthly-shared", "本月共享额度", Math.Clamp(100 * (1 - used.Value), 0, 100), null, null,
            "%", null, 2592000, "used", "exact", observed, false, DisplayText: codeText)];
    }
}
