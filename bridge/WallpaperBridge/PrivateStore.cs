using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WallpaperBridge;

public sealed class PrivateStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Root { get; }
    public string ReadToken { get; }
    private readonly object gate = new();
    private BridgeConfig config;
    private readonly HashSet<string> cacheFaults = [];
    public string[] CacheFaults { get { lock (gate) return cacheFaults.ToArray(); } }
    public BridgeConfig Config { get { lock (gate) return config; } }
    public long Revision { get; private set; }

    public PrivateStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallpaperDashboard", "private");
        Directory.CreateDirectory(Root);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
        config = Read<BridgeConfig>("config") ?? BridgeConfig.Default;
        ReadToken = Read<string>("read-token") ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Write("read-token", ReadToken);
        // This limited ticket is intentionally usable by local install scripts. It is never a CPAMP credential.
        File.WriteAllText(Path.Combine(Root, "read-token.txt"), ReadToken, new UTF8Encoding(false));
    }

    public void Save(BridgeConfig value)
    {
        Validate(value);
        lock (gate) { Write("config", value); config = value; Revision++; }
    }
    public static void Validate(BridgeConfig value)
    {
        if (value.Port is < 1024 or > 65535) throw new ArgumentException("端口必须在 1024–65535");
        if (!Uri.TryCreate(value.CpampUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("CPAMP 地址必须是无用户名、查询参数或片段的 HTTPS 地址");
        if (!double.IsFinite(value.City.Latitude) || !double.IsFinite(value.City.Longitude) || Math.Abs(value.City.Latitude) > 90 || Math.Abs(value.City.Longitude) > 180 || string.IsNullOrWhiteSpace(value.City.Name))
            throw new ArgumentException("请选择有效城市");
        try { TimeZoneInfo.FindSystemTimeZoneById(value.City.Timezone); } catch { throw new ArgumentException("城市时区无效"); }
        if (value.Accounts.Length > 100 || value.Accounts.Any(a => a.Alias.Length > 60 || a.Id.Length > 128)) throw new ArgumentException("账号配置超出范围");
        if (value.QuotaRefreshMinutes is < 1 or > 10080) throw new ArgumentException("用量刷新间隔需为 1–10080 分钟");
        if (value.ManagementKey?.Length > 4096) throw new ArgumentException("凭据长度无效");
        if (value.Clock is { } clock) ClockSettings.Validate(clock);
        if (value.Appearance is { } appearance && appearance != appearance.Normalize()) throw new ArgumentException("壁纸主题无效");
        WeatherProviders.ValidateSettings(value.Weather ?? new());
        AiSourceSettings.Validate(value.AiSources ?? new());
    }
    public T? Read<T>(string name)
    {
        lock (gate)
        {
            var path = Path.Combine(Root, name + ".dpapi");
            if (!File.Exists(path)) return default;
            // Do not overwrite an unreadable existing configuration with empty credentials.
            var data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(data, Json);
        }
    }
    // Only rebuildable caches use this boundary. Config and display tickets stay strict.
    public T? ReadCache<T>(string name, Func<T, bool>? validate = null)
    {
        ValidateCacheName(name);
        try
        {
            var value = Read<T>(name);
            if (value is not null && validate is not null && !validate(value)) throw new JsonException("Invalid cache shape");
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            lock (gate) cacheFaults.Add(name + ":read");
            return default;
        }
    }
    public bool WriteCache<T>(string name, T value)
    {
        ValidateCacheName(name);
        try { Write(name, value); lock (gate) { cacheFaults.Remove(name + ":write"); cacheFaults.Remove(name + ":read"); } return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        { lock (gate) cacheFaults.Add(name + ":write"); return false; }
    }
    private static void ValidateCacheName(string name)
    {
        if (name is not ("quota-cache" or "weather-cache" or "quota-refresh")) throw new ArgumentException("Not a rebuildable cache");
    }
    public void Write<T>(string name, T data)
    {
        lock (gate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Json);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            var path = Path.Combine(Root, name + ".dpapi");
            File.WriteAllBytes(path + ".tmp", encrypted);
            File.Move(path + ".tmp", path, true);
        }
    }
}
