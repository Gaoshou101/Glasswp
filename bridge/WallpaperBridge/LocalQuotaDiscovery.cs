namespace WallpaperBridge;

// All groups are independent: one unreadable client must not hide another client's quota.
internal sealed class LocalQuotaDiscovery(HttpClient client, string? userHome = null)
{
    private readonly LocalAgentQuotaReader agents = new(client, userHome);
    private readonly LocalCopilotQuotaReader copilot = new(client, userHome);
    private readonly LocalGoogleQuotaReader google = new(client, userHome);
    private readonly LocalEncryptedQuotaReader encrypted = new(client, userHome);
    private readonly LocalKiroQuotaReader kiro = new(client, userHome);
    private readonly LocalZCodeQuotaReader zcode = new(client, userHome);
    private readonly LocalKimiWorkQuotaReader kimi = new(client, userHome);
    private readonly LocalTeleAgentQuotaReader teleagent = new(client, userHome);
    private readonly LocalAutoClawQuotaReader autoclaw = new(client, userHome);
    private readonly LocalDesktopQuotaReader desktop = new(client, userHome);
    private readonly LocalFileQuotaReader files = new(client, userHome);

    public async Task<QuotaData> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var results = await Task.WhenAll(
            Group("Codex / Claude Code", ct => agents.Read(true, true, ct), token),
            Group("GitHub Copilot", copilot.Read, token),
            Group("Google", google.Read, token),
            Group("Warp / Agnes Code / AStudio", encrypted.Read, token),
            Group("Kiro", kiro.Read, token),
            Group("Z Code", zcode.Read, token),
            Group("Kimi Work", kimi.Read, token),
            Group("TeleAgent", teleagent.Read, token),
            Group("AutoClaw", autoclaw.Read, token),
            Group("桌面 AI 客户端", desktop.Read, token),
            Group("本地 AI 客户端", files.Read, token));
        return Combine(results);
    }

    internal static async Task<QuotaData> Group(string label, Func<CancellationToken, Task<QuotaData>> read, CancellationToken token)
    {
        try { return await read(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return new("offline", null, label + "：本地登录或额度暂不可读取", []); }
    }

    internal static QuotaData Combine(IEnumerable<QuotaData> results)
    {
        var all = results.ToArray();
        var available = all.Where(result => result.Status != "unconfigured").ToArray();
        var healthy = available.Count(result => result.Status == "ok");
        var status = available.Length == 0 ? "unconfigured" : healthy == available.Length ? "ok"
            : healthy > 0 || available.Any(result => result.Status == "stale") ? "stale"
            : available.Any(result => result.Status == "unauthorized") ? "unauthorized" : "offline";
        var accounts = all.SelectMany(result => result.Accounts).DistinctBy(account => account.Id).ToArray();
        var fetched = all.Select(result => result.FetchedAt).Where(date => date is not null).Max();
        // Keep each group's diagnostic, including unsupported stores, without credential or path details.
        return new(status, fetched, available.Length == 0 ? "未检测到支持的本机 AI 登录；请先在原客户端登录后刷新" : string.Join("；", available.Select(result => result.Message).Where(message => message.Length > 0).Distinct()), accounts);
    }
}
