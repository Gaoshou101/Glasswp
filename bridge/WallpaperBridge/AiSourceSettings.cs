namespace WallpaperBridge;

public sealed record AiSourceSettings(bool CodexEnabled = false, bool ClaudeEnabled = false,
    bool MagpieEnabled = false, string MagpieUrl = "http://127.0.0.1:3425", string? MagpieApiKey = null, string? Source = null,
    AiSourceOptions? LocalOptions = null, AiSourceOptions? MagpieOptions = null,
    string CpaUrl = "", string? CpaKey = null, string CpaApiVersion = "v0", AiSourceOptions? CpaOptions = null,
    string Sub2apiUrl = "", string? Sub2apiKey = null, AiSourceOptions? Sub2apiOptions = null)
{
    // Old installations had independent switches. Resolve once to one source; new saves always write Source.
    public string EffectiveSource => Source ?? (MagpieEnabled ? "magpie" : CodexEnabled || ClaudeEnabled ? "local" : "cpamp");
    public static readonly string[] ReaderSources = ["local", "magpie", "cpa", "sub2api"];
    public bool AnyEnabled => ReaderSources.Contains(EffectiveSource);
    public static bool UsesCpamp(BridgeConfig config) => (config.AiSources ?? new()).EffectiveSource == "cpamp";
    public AiSourceOptions Options(string source) => source switch {
        "magpie" => MagpieOptions ?? new(RefreshMinutes: 1), "cpa" => CpaOptions ?? new(),
        "sub2api" => Sub2apiOptions ?? new(), _ => LocalOptions ?? new()
    };
    // Display and scheduling edits must not invalidate quota evidence or an in-flight read.
    public bool SameConnection(AiSourceSettings? other) => other is not null && EffectiveSource == other.EffectiveSource &&
        (EffectiveSource switch {
            "magpie" => MagpieUrl == other.MagpieUrl && MagpieApiKey == other.MagpieApiKey,
            "cpa" => CpaUrl == other.CpaUrl && CpaKey == other.CpaKey && CpaApiVersion == other.CpaApiVersion,
            "sub2api" => Sub2apiUrl == other.Sub2apiUrl && Sub2apiKey == other.Sub2apiKey, _ => true
        });
    public static void Validate(AiSourceSettings value)
    {
        if (value.Source is not (null or "cpamp" or "local" or "magpie" or "cpa" or "sub2api")) throw new ArgumentException("请选择有效的 AI 套餐来源");
        if (!MagpieQuotaReader.ValidateUrl(value.MagpieUrl)) throw new ArgumentException("Magpie 地址须为 HTTPS 或本机回环 HTTP 地址，且不含凭据、查询参数或片段");
        if (value.MagpieApiKey is { } key && (key.Length > 4096 || key.Any(char.IsControl))) throw new ArgumentException("Magpie API Key 格式无效");
        foreach (var source in ReaderSources) AiSourceOptions.Validate(value.Options(source));
        foreach (var (name, url, credential) in new[] { ("CPA", value.CpaUrl, value.CpaKey), ("Sub2API", value.Sub2apiUrl, value.Sub2apiKey) }) {
            if (url is null || url.Length > 0 && !MagpieQuotaReader.ValidateUrl(url)) throw new ArgumentException(name + " 地址须为 HTTPS 或本机回环 HTTP 根地址");
            if (credential is not null && (credential.Length > 4096 || credential.Any(char.IsControl))) throw new ArgumentException(name + " 管理密钥格式无效");
        }
        if (value.CpaApiVersion is not ("v0" or "v8")) throw new ArgumentException("CPA 管理接口版本须为 v0 或 v8");
    }
}

public sealed record AiSourceSettingsInput(bool CodexEnabled = false, bool ClaudeEnabled = false,
    bool MagpieEnabled = false, string? MagpieUrl = null, string? MagpieApiKey = null, bool ClearMagpieApiKey = false, string? Source = null,
    AiSourceOptions? LocalOptions = null, AiSourceOptions? MagpieOptions = null,
    string? CpaUrl = null, string? CpaKey = null, bool ClearCpaKey = false, string? CpaApiVersion = null, AiSourceOptions? CpaOptions = null,
    string? Sub2apiUrl = null, string? Sub2apiKey = null, bool ClearSub2apiKey = false, AiSourceOptions? Sub2apiOptions = null)
{
    public AiSourceSettings Apply(AiSourceSettings? saved)
    {
        saved ??= new();
        var url = (MagpieUrl ?? saved.MagpieUrl).Trim().TrimEnd('/');
        var key = ClearMagpieApiKey ? null : MagpieApiKey?.Trim() ?? (url == saved.MagpieUrl.TrimEnd('/') ? saved.MagpieApiKey : null);
        var value = new AiSourceSettings(CodexEnabled, ClaudeEnabled, MagpieEnabled, url, key, Source ?? saved.EffectiveSource,
            LocalOptions ?? saved.LocalOptions, url == saved.MagpieUrl.TrimEnd('/') ? MagpieOptions ?? saved.MagpieOptions :
                (MagpieOptions ?? saved.MagpieOptions ?? new(RefreshMinutes: 1)) with { Accounts = null });
        var cpaUrl = (CpaUrl ?? saved.CpaUrl).Trim().TrimEnd('/');
        var subUrl = (Sub2apiUrl ?? saved.Sub2apiUrl).Trim().TrimEnd('/');
        var cpaKey = ClearCpaKey ? null : CpaKey?.Trim() ?? (cpaUrl == saved.CpaUrl ? saved.CpaKey : null);
        var subKey = ClearSub2apiKey ? null : Sub2apiKey?.Trim() ?? (subUrl == saved.Sub2apiUrl ? saved.Sub2apiKey : null);
        value = value with {
            CpaUrl = cpaUrl, CpaKey = cpaKey, CpaApiVersion = CpaApiVersion ?? saved.CpaApiVersion,
            CpaOptions = cpaUrl == saved.CpaUrl && cpaKey == saved.CpaKey ? CpaOptions ?? saved.CpaOptions : (CpaOptions ?? saved.CpaOptions ?? new()) with { Accounts = null },
            Sub2apiUrl = subUrl, Sub2apiKey = subKey,
            Sub2apiOptions = subUrl == saved.Sub2apiUrl && subKey == saved.Sub2apiKey ? Sub2apiOptions ?? saved.Sub2apiOptions : (Sub2apiOptions ?? saved.Sub2apiOptions ?? new()) with { Accounts = null }
        };
        AiSourceSettings.Validate(value);
        return value;
    }
}

public sealed record AiAccountSelection(string Id, string Alias = "");
public sealed record AiSourceOptions(bool AutoRefreshEnabled = true, int RefreshMinutes = 10, AiAccountSelection[]? Accounts = null)
{
    // null means automatically show all discovered accounts; [] intentionally shows none.
    public static void Validate(AiSourceOptions value)
    {
        if (value.RefreshMinutes is < 1 or > 10080) throw new ArgumentException("AI 来源刷新间隔需为 1–10080 分钟");
        if (value.Accounts is { } accounts && (accounts.Length > 200 ||
            accounts.Any(a => a is null || string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 256 || a.Id.Any(char.IsControl) || a.Alias is null || a.Alias.Length > 60 || a.Alias.Any(char.IsControl)) ||
            accounts.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != accounts.Length))
            throw new ArgumentException("AI 账号配置无效：账号不得重复，别名最多 60 字");
    }
    public QuotaData Display(QuotaData data) => Accounts is null ? data : data with {
        Accounts = Accounts.Select(s => data.Accounts.FirstOrDefault(a => a.Id == s.Id) is { } a
            ? a with { Alias = string.IsNullOrWhiteSpace(s.Alias) ? a.Alias : s.Alias.Trim() } : null).OfType<QuotaAccount>().ToArray()
    };
}
