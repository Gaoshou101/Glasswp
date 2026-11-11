namespace WallpaperBridge;

// Exactly one source is active; native credentials never enter CPAMP configuration.
public sealed class AiQuotaSources : BackgroundService
{
    private readonly PrivateStore store;
    private readonly DataHub hub;
    private readonly HttpClient client;
    private readonly LocalQuotaDiscovery local;
    private readonly MagpieQuotaReader magpie;
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<string, (AiSourceSettings Settings, QuotaRefreshState State)> progress = [];
    private AiSourceSettings? active;
    private readonly Dictionary<string, DateTimeOffset> nextReads = [], lastReads = [];
    private readonly CpaQuotaReader cpa;
    private readonly Sub2apiQuotaReader sub2api;

    public AiQuotaSources(PrivateStore store, DataHub hub, HttpMessageHandler? handler = null, string? userHome = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.store = store; this.hub = hub;
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        local = new(client, userHome); magpie = new(client); cpa = new(client); sub2api = new(client); clock = utcNow ?? (() => DateTimeOffset.UtcNow);
    }
    public void Queue()
    {
        var settings = store.Config.AiSources ?? new();
        if (!settings.AnyEnabled) return;
        lock (gate)
        {
            var source = settings.EffectiveSource;
            if (progress.TryGetValue(source, out var current) && settings.SameConnection(current.Settings) && current.State.Status is "queued" or "running") return;
            var next = lastReads.GetValueOrDefault(source).AddSeconds(60);
            if (next < clock()) next = clock();
            nextReads[source] = next;
            var previous = settings.SameConnection(current.Settings) ? current.State : new QuotaRefreshState();
            progress[source] = (settings, previous with { Status = "queued", Message = next > clock() ? "已排队，最短 60 秒查询间隔结束后读取" : "已排队，正在准备读取额度" });
        }
    }
    public QuotaRefreshReport RefreshStatus()
    {
        var settings = store.Config.AiSources ?? new(); var source = settings.EffectiveSource;
        lock (gate)
        {
            var state = progress.TryGetValue(source, out var entry) && settings.SameConnection(entry.Settings) ? entry.State : new QuotaRefreshState(Message: "等待首次读取额度");
            var next = nextReads.GetValueOrDefault(source);
            var options = settings.Options(source);
            return new(settings.AnyEnabled && options.AutoRefreshEnabled, options.RefreshMinutes, !settings.AnyEnabled || next == default || !options.AutoRefreshEnabled && state.Status != "queued" ? null : next, state);
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var work = AiSourceSettings.ReaderSources.ToDictionary(s => s, _ => Task.CompletedTask);
        var cancellations = new Dictionary<string, CancellationTokenSource>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = store.Config.AiSources ?? new();
                lock (gate)
                {
                    if (active != settings)
                    {
                        var source = settings.EffectiveSource;
                        var last = lastReads.GetValueOrDefault(source);
                        var connectionChanged = !settings.SameConnection(active);
                        if (connectionChanged && active?.AnyEnabled == true && cancellations.TryGetValue(active.EffectiveSource, out var oldRead)) oldRead.Cancel();
                        var scheduleChanged = active is null || active.Options(source).RefreshMinutes != settings.Options(source).RefreshMinutes ||
                            active.Options(source).AutoRefreshEnabled != settings.Options(source).AutoRefreshEnabled;
                        var queued = progress.TryGetValue(source, out var pending) && settings.SameConnection(pending.Settings) && pending.State.Status == "queued";
                        if (connectionChanged || scheduleChanged && !queued) {
                            var next = connectionChanged ? last.AddSeconds(60) : last.AddMinutes(settings.Options(source).RefreshMinutes);
                            if (next < clock()) next = clock();
                            nextReads[source] = next;
                        }
                        active = settings;
                    }
                }
                var selected = settings.EffectiveSource;
                if (settings.AnyEnabled && work[selected].IsCompleted) {
                    bool due;
                    lock (gate) due = clock() >= nextReads.GetValueOrDefault(selected) && DueEnabled(settings, selected);
                    if (due) {
                        try { await work[selected]; } catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                        if (cancellations.Remove(selected, out var previousRead)) previousRead.Dispose();
                        var readToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        cancellations[selected] = readToken;
                        work[selected] = ReadSource(settings, readToken.Token);
                    }
                }
                await Task.Delay(500, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var cancellation in cancellations.Values) cancellation.Cancel();
            try { await Task.WhenAll(work.Values); }
            catch (OperationCanceledException) { }
            foreach (var cancellation in cancellations.Values) cancellation.Dispose();
            client.Dispose();
        }
    }
    private bool DueEnabled(AiSourceSettings settings, string source) => settings.Options(source).AutoRefreshEnabled ||
        progress.TryGetValue(source, out var pending) && settings.SameConnection(pending.Settings) && pending.State.Status == "queued";
    private async Task ReadSource(AiSourceSettings settings, CancellationToken token)
    {
        var source = settings.EffectiveSource;
        var started = clock();
        DateTimeOffset? lastSuccess;
        lock (gate)
        {
            lastSuccess = progress.TryGetValue(source, out var previous) && settings.SameConnection(previous.Settings) ? previous.State.LastSuccessAt : null;
            lastReads[source] = started; nextReads[source] = started.AddMinutes(settings.Options(source).RefreshMinutes);
            progress[source] = (settings, new(LastAttemptAt: started, LastSuccessAt: lastSuccess, Status: "running", Message: "正在读取" + (source == "local" ? "本机 AI 套餐和额度" : source + " 套餐余量")));
        }
        QuotaData data;
        try { data = source switch {
            "local" => await local.Read(token), "magpie" => await magpie.Read(settings.MagpieUrl, settings.MagpieApiKey, token),
            "cpa" => await cpa.Read(settings, token), "sub2api" => await sub2api.Read(settings, token),
            _ => new("unconfigured", null, "请选择 AI 来源", [])
        }; }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (gate) if (progress.TryGetValue(source, out var cancelled) && settings.SameConnection(cancelled.Settings)) progress.Remove(source);
            throw;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { data = new("offline", null, "套餐和额度暂不可读取，请检查所选来源", []); }
        lock (gate)
        {
            // A late reply from a different source or gateway never changes the active report or snapshot.
            if (!settings.SameConnection(store.Config.AiSources ?? new()))
            {
                if (progress.TryGetValue(source, out var superseded) && settings.SameConnection(superseded.Settings)) progress.Remove(source);
                return;
            }
            var finished = clock();
            var status = data.Status is "ok" or "empty" ? "ok" : data.Status == "stale" && data.Accounts.Length > 0 ? "partial" : "failed";
            // The public envelope and completion stamp are identical; frontend snapshot waiting can finish immediately.
            if (status is "ok" or "partial") data = data with { FetchedAt = finished };
            hub.AiQuota(source, settings, data);
            progress[source] = (settings, new(started, status is "ok" or "partial" ? finished : lastSuccess, finished, status, data.Message));
        }
    }
    internal Task ReadOnce(CancellationToken token)
    {
        var settings = store.Config.AiSources ?? new();
        return settings.AnyEnabled ? ReadSource(settings, token) : Task.CompletedTask;
    }
}
