using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WiseSphere.Core;
using WiseSphere.Glpi;

namespace WiseSphere.Integration.Services;

/// <summary>One async lock per key (client code): events of one client are processed in order, different clients in parallel.
/// This is what keeps dedupe and storm grouping race-free without slowing down the whole platform.</summary>
public sealed class KeyedLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) sem.Release(); }
    }
}

/// <summary>GLPI ids looked up by name once (request types, groups, solution type) - no id files to keep in sync.</summary>
public sealed class GlpiCatalog(GlpiClient glpi, IOptions<IntegrationOptions> options, ILogger<GlpiCatalog> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Ids? _ids;

    public sealed record Ids(int Monitoring, int Security, int Noc, int Soc, int Sdm, int SolutionType);

    public async Task<Ids> GetAsync(CancellationToken ct)
    {
        if (_ids is not null) return _ids;
        await _gate.WaitAsync(ct);
        try
        {
            if (_ids is not null) return _ids;
            var n = options.Value.Names;
            var c = options.Value.Ids;
            async Task<int> Resolve(int configured, string type, string name)
            {
                if (configured > 0) return configured;
                try
                {
                    var id = await glpi.FindIdByNameAsync(type, name, ct);
                    if (id is null) log.LogWarning("GLPI {Type} '{Name}' not found - run glpi/provision_glpi.py", type, name);
                    return id ?? 0;
                }
                catch (GlpiException e)
                {
                    log.LogWarning("Cannot look up GLPI {Type} '{Name}' ({Error}); set Integration:Ids instead", type, name, e.Message);
                    return 0;
                }
            }
            _ids = new Ids(
                await Resolve(c.RequestTypeMonitoring, "RequestType", n.RequestTypeMonitoring),
                await Resolve(c.RequestTypeSecurity, "RequestType", n.RequestTypeSecurity),
                await Resolve(c.GroupNoc, "Group", n.GroupNoc), await Resolve(c.GroupSoc, "Group", n.GroupSoc),
                await Resolve(c.GroupSdm, "Group", n.GroupSdm), await Resolve(c.SolutionType, "SolutionType", n.SolutionType));
            log.LogInformation("GLPI catalog loaded: {@Ids}", _ids);
            return _ids;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Provider-agnostic SMS sender (D-04). Never throws: a failed SMS must not block ticket creation.</summary>
public sealed class SmsSender(HttpClient http, IOptions<SmsOptions> options, Metrics metrics, ILogger<SmsSender> log)
{
    public async Task<bool> SendAsync(Notification n, CancellationToken ct)
    {
        var o = options.Value;
        if (o.Url.Length == 0) { log.LogWarning("SMS not configured, dropped: {Message}", n.Message); return false; }
        var to = o.Numbers.GetValueOrDefault(n.Channel) ?? o.Numbers.GetValueOrDefault("noc") ?? "";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                // Buffered body with Content-Length: some SMS gateways reject chunked requests
                using var req = new HttpRequestMessage(HttpMethod.Post, o.Url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { to, message = n.Message, source = "wisesphere" }), Encoding.UTF8, "application/json"),
                };
                if (o.Key.Length > 0) req.Headers.Add(o.KeyHeader, o.Key);
                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) { metrics.Inc("ws_sms_sent_total", n.Channel); return true; }
                log.LogWarning("SMS provider HTTP {Status} (attempt {Attempt})", (int)res.StatusCode, attempt);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning(e, "SMS send failed (attempt {Attempt})", attempt);
            }
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }
        metrics.Inc("ws_sms_failed_total", n.Channel);
        return false;
    }
}

/// <summary>INT-07: page the integration engineer when processing fails - at most once per 10 minutes per error kind.</summary>
public sealed class FailureNotifier(SmsSender sms, ILogger<FailureNotifier> log)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new();

    public void Report(string where, Exception e)
    {
        log.LogError(e, "Processing failed in {Where}", where);
        var key = $"{where}:{e.GetType().Name}";
        var now = DateTimeOffset.UtcNow;
        if (_last.TryGetValue(key, out var t) && now - t < TimeSpan.FromMinutes(10)) return;
        _last[key] = now;
        _ = sms.SendAsync(new("integration", Text.Truncate($"WiseSphere integration FAILED in {where}: {e.Message}", 300)), CancellationToken.None);
    }
}

/// <summary>Small JSON file store for state that must survive restarts (SMS dedupe, processed surveys).</summary>
public sealed class StateStore(IOptions<IntegrationOptions> options)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> LoadAsync<T>(string name, CancellationToken ct) where T : new()
    {
        var path = Path.Combine(options.Value.StateDir, name + ".json");
        if (!File.Exists(path)) return new T();
        await using var f = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(f, cancellationToken: ct) ?? new T();
    }

    public async Task SaveAsync<T>(string name, T value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(options.Value.StateDir);
            var path = Path.Combine(options.Value.StateDir, name + ".json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value, JsonOpts), ct);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Minimal Prometheus counters for platform self-monitoring (NFR-OPS-01): GET /metrics.</summary>
public sealed class Metrics
{
    private readonly ConcurrentDictionary<(string Name, string Label), long> _counters = new();
    private readonly ConcurrentDictionary<string, (long Count, double SumMs)> _timings = new();

    public void Inc(string name, string label = "") => _counters.AddOrUpdate((name, label), 1, (_, v) => v + 1);

    public void Observe(string name, TimeSpan elapsed) =>
        _timings.AddOrUpdate(name, (1, elapsed.TotalMilliseconds), (_, v) => (v.Count + 1, v.SumMs + elapsed.TotalMilliseconds));

    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var g in _counters.GroupBy(c => c.Key.Name).OrderBy(g => g.Key))
        {
            sb.Append("# TYPE ").Append(g.Key).Append(" counter\n");
            foreach (var c in g) sb.Append(g.Key).Append(c.Key.Label.Length > 0 ? $"{{kind=\"{c.Key.Label}\"}}" : "").Append(' ').Append(c.Value).Append('\n');
        }
        foreach (var t in _timings.OrderBy(t => t.Key))
        {
            sb.Append("# TYPE ").Append(t.Key).Append("_ms summary\n");
            sb.Append(t.Key).Append("_ms_count ").Append(t.Value.Count).Append('\n');
            sb.Append(t.Key).Append("_ms_sum ").Append(t.Value.SumMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }
}
