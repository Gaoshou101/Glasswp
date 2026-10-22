using System.Text.Json;
using System.Text.Json.Nodes;

namespace WallpaperBridge;

/// <summary>Read-only compatibility for response-header windows from stock CPAMP.</summary>
public sealed class LocalQuotaClassifier
{
    private readonly Dictionary<string, (string Scope, DateTimeOffset Until)> cache = [];

    public async Task<JsonElement> Classify(JsonElement response, AccountSelection account, DateTimeOffset now,
        Func<object, CancellationToken, Task<JsonElement>> readEvents, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (account.Provider != "codex") return response;
        var root = QuotaAdapter.Unwrap(response);
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return response;
        JsonNode? copy = null;
        var correctedIds = new HashSet<string>();
        var reads = new Dictionary<long, JsonElement>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        foreach (var (item, itemIndex) in items.EnumerateArray().Select((value, index) => (value, index)))
        {
            if (QuotaAdapter.Text(item, "row_key") != account.Id || !item.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array) continue;
            foreach (var (window, windowIndex) in windows.EnumerateArray().Select((value, index) => (value, index)))
            {
                if (!Candidate(window, out var observed)) continue;
                var key = account.Id + "\n" + account.AuthFile + "\n" + window.GetRawText();
                string scope;
                if (cache.TryGetValue(key, out var previous) && previous.Until > now) scope = previous.Scope;
                else
                {
                    try
                    {
                        if (!reads.TryGetValue(observed, out var events))
                        {
                            if (reads.Count >= 8 || budget.IsCancellationRequested) continue;
                            events = await readEvents(new {
                                from_ms = observed, to_ms = observed + 1, now_ms = now.ToUnixTimeMilliseconds(),
                                filters = new { providers = new[] { account.Provider }, auth_files = new[] { account.AuthFile }, include_failed = true },
                                include = new { events_page = new { limit = 100 } }
                            }, budget.Token);
                            reads[observed] = events;
                        }
                        scope = Match(window, events, account, observed);
                        if (cache.Count >= 128) cache.Remove(cache.First().Key);
                        cache[key] = (scope, now.AddMinutes(scope.Length > 0 ? 10 : 1));
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                    catch (Exception e) when (e is HttpRequestException or JsonException) { continue; }
                }
                if (scope.Length == 0) continue;
                copy ??= JsonNode.Parse(root.GetRawText())!;
                var corrected = copy["items"]![itemIndex]!["windows"]![windowIndex]!;
                corrected["model_scope_kind"] = "feature";
                corrected["model_scope_key"] = scope;
                var id = QuotaAdapter.Text(window, "provider_window_id");
                var correctedId = id.StartsWith("window-", StringComparison.Ordinal)
                    ? scope.Replace('_', '-') + "-0-" + id : scope.Replace('_', '-') + "-" + id + "-0";
                corrected["provider_window_id"] = correctedId;
                correctedIds.Add(correctedId);
            }
        }
        if (copy is not null)
        {
            // A server that previously stored the correct feature scope can return
            // both that window and a new legacy window. Keep the newest whole
            // observation for the same feature ID, without mixing reset fields.
            foreach (var item in copy["items"]!.AsArray().OfType<JsonObject>().Where(item => item["row_key"]?.GetValue<string>() == account.Id))
            {
                var windows = item["windows"]!.AsArray();
                foreach (var group in windows.OfType<JsonObject>().Where(w => correctedIds.Contains(w["provider_window_id"]?.GetValue<string>() ?? ""))
                    .GroupBy(w => (w["provider_window_id"]!.GetValue<string>(), w["model_scope_key"]?.GetValue<string>())).Where(g => g.Count() > 1).ToArray())
                {
                    var newest = group.OrderByDescending(w => BalanceTime(JsonSerializer.SerializeToElement(w))).ThenByDescending(w => w["observed_at_ms"]?.GetValue<long>() ?? 0).First();
                    foreach (var duplicate in group.Where(w => !ReferenceEquals(w, newest)).ToArray()) windows.Remove(duplicate);
                }
            }
        }
        return copy is null ? response : JsonSerializer.SerializeToElement(copy);
    }

    private static double? BalanceTime(JsonElement window)
    {
        if (window.TryGetProperty("field_sources", out var fields) && fields.ValueKind == JsonValueKind.Object)
        {
            var key = QuotaAdapter.Number(window, "remaining_percent") is not null ? "remaining_percent" : "used_percent";
            if ((fields.TryGetProperty("quota", out var quota) || fields.TryGetProperty(key, out quota)) && quota.ValueKind == JsonValueKind.Object)
                return QuotaAdapter.Number(quota, "observed_at_ms");
        }
        return QuotaAdapter.Number(window, "observed_at_ms");
    }

    private static bool Candidate(JsonElement window, out long observed)
    {
        observed = 0;
        if (QuotaAdapter.Text(window, "availability") is "inactive" or "removed" or "missing" ||
            QuotaAdapter.Text(window, "model_scope_key") is not ("" or "all" or "codex_main") ||
            QuotaAdapter.Text(window, "model_scope_kind") is not ("" or "all" or "family") ||
            QuotaAdapter.Number(window, "duration_seconds") != 86400) return false;
        var source = window;
        if (window.TryGetProperty("field_sources", out var fields) && fields.ValueKind == JsonValueKind.Object)
        {
            var key = QuotaAdapter.Number(window, "remaining_percent") is not null ? "remaining_percent" : "used_percent";
            if ((fields.TryGetProperty("quota", out var quota) || fields.TryGetProperty(key, out quota)) && quota.ValueKind == JsonValueKind.Object) source = quota;
        }
        if (QuotaAdapter.Text(source, "source") != "response_header" || QuotaAdapter.Timestamp(source, "observed_at_ms") is not { } at) return false;
        observed = at.ToUnixTimeMilliseconds();
        return true;
    }

    private static string Match(JsonElement window, JsonElement response, AccountSelection account, long observed)
    {
        var root = QuotaAdapter.Unwrap(response);
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Object ||
            events.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True ||
            !events.TryGetProperty("items", out var rows) || rows.ValueKind != JsonValueKind.Array) return "";
        // The top-level ID may describe a newer reset boundary. Only use it when
        // the balance and top-level observation refer to the same instant.
        var id = QuotaAdapter.Number(window, "observed_at_ms") == observed ? QuotaAdapter.Text(window, "source_observation_id") : "";
        var matches = new List<string>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || QuotaAdapter.Text(row, "auth_file_snapshot") != account.AuthFile ||
                QuotaAdapter.Text(row, "auth_provider_snapshot") != account.Provider || QuotaAdapter.Number(row, "timestamp_ms") != observed ||
                !row.TryGetProperty("response_metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("quota", out var quota) || quota.ValueKind != JsonValueKind.Object) continue;
            var trace = metadata.TryGetProperty("trace", out var traceNode) && traceNode.ValueKind == JsonValueKind.Object ? QuotaAdapter.Text(traceNode, "primary_trace_id") : "";
            if (id.Length > 0 && id != QuotaAdapter.Text(row, "request_id") && id != QuotaAdapter.Text(row, "event_hash") && id != trace) continue;
            var used = QuotaAdapter.Number(window, "remaining_percent") is { } remaining ? 100 - remaining : QuotaAdapter.Number(window, "used_percent");
            if (used is not (>= 0 and <= 100)) continue;
            var matchingWindows = new[] { "primary", "secondary" }.Count(name => quota.TryGetProperty(name, out var header) && header.ValueKind == JsonValueKind.Object &&
                QuotaAdapter.Number(header, "window_minutes") * 60 == QuotaAdapter.Number(window, "duration_seconds") &&
                QuotaAdapter.Number(header, "used_percent") is { } percent && Math.Abs(percent - used.Value) < 0.0001);
            if (matchingWindows != 1) continue;
            var active = QuotaAdapter.Text(quota, "active_limit").Trim().ToLowerInvariant();
            var scope = active == "imagegen" || active.StartsWith("imagegen_", StringComparison.Ordinal) ? active : "";
            if (active.Length == 0)
            {
                var model = new[] { "resolved_model", "analytics_model", "model", "requested_model" }.Select(key => QuotaAdapter.Text(row, key).Trim().ToLowerInvariant()).FirstOrDefault(value => value.Length > 0) ?? "";
                if (model.StartsWith("gpt-image-", StringComparison.Ordinal) || model.StartsWith("dall-e-", StringComparison.Ordinal)) scope = "imagegen";
            }
            if (scope.Length > 64 || scope.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) scope = "";
            matches.Add(scope);
        }
        // Ambiguous or missing provenance must never rename an unrelated daily budget.
        return matches.Count == 1 ? matches[0] : "";
    }
}
