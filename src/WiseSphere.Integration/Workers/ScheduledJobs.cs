using Microsoft.Extensions.Options;
using WiseSphere.Core;
using WiseSphere.Glpi;
using WiseSphere.Integration.Services;

namespace WiseSphere.Integration.Workers;

/// <summary>INT-04 SLA watch: SMS at T-15 min and at breach of time-to-own / time-to-resolve.</summary>
public sealed class SlaWatchJob(GlpiClient glpi, PlatformPolicy policy, SmsSender sms, StateStore state, Metrics metrics, ILogger<SlaWatchJob> log)
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        try
        {
            var candidates = new List<SlaCandidate>();
            foreach (var (kind, stage) in new[] { (SlaKind.TimeToResolve, SlaStage.Breached), (SlaKind.TimeToResolve, SlaStage.AtRisk),
                                                   (SlaKind.TimeToOwn, SlaStage.Breached), (SlaKind.TimeToOwn, SlaStage.AtRisk) })
                candidates.AddRange(await glpi.SearchSlaAsync(kind, stage, policy.AtRiskMinutes, ct));

            var sent = await state.LoadAsync<Dictionary<string, DateTimeOffset>>("sla-sent", ct);
            var alerts = SlaDecider.Decide(candidates, policy, sent, DateTimeOffset.UtcNow);
            foreach (var a in alerts) await sms.SendAsync(a.Notification, ct);
            await state.SaveAsync("sla-sent", sent, ct);
            metrics.Inc("ws_sla_alerts_total", alerts.Count.ToString());
            log.LogInformation("SLA watch: {Candidates} candidates, {Alerts} SMS", candidates.Count, alerts.Count);
            return alerts.Count;
        }
        finally { _running.Release(); }
    }
}

/// <summary>INT-03 CSAT review: low survey scores become private service-recovery tasks for the SDM group.</summary>
public sealed class CsatReviewJob(GlpiClient glpi, GlpiCatalog catalog, StateStore state, IOptions<IntegrationOptions> options, ILogger<CsatReviewJob> log)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var ids = await catalog.GetAsync(ct);
        var processed = await state.LoadAsync<HashSet<int>>("csat-processed", ct);
        var writes = CsatDecider.Decide(await glpi.GetRecentSurveysAsync(ct), processed, ids.Sdm, options.Value.CsatLowScore);
        foreach (var w in writes) await glpi.CreateAsync(w, ct);
        await state.SaveAsync("csat-processed", processed, ct);
        log.LogInformation("CSAT review: {Tasks} service-recovery tasks", writes.Count);
        return writes.Count;
    }
}

/// <summary>Runs the scheduled jobs. Failures are reported (INT-07) and retried on the next tick.</summary>
public sealed class SchedulerWorker(
    SlaWatchJob sla, CsatReviewJob csat, ContainmentService containment, PlatformPolicy policy, FailureNotifier failures,
    IOptions<IntegrationOptions> options, ILogger<SchedulerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var csatAt = TimeOnly.Parse(o.CsatReviewTime);
        var cal = policy.Calendars.Values.FirstOrDefault();
        var tz = TimeZoneInfo.FindSystemTimeZoneById(cal?.TimeZone ?? "UTC");
        DateOnly? csatDoneFor = null;
        log.LogInformation("Scheduler started: SLA watch every {Min} min, CSAT review daily at {At} {Tz}", o.SlaWatchIntervalMinutes, csatAt, tz.Id);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, o.SlaWatchIntervalMinutes)));
        do
        {
            if (o.SlaWatchEnabled)
            {
                try { await sla.RunAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException) { failures.Report("sla-watch", e); }
            }
            try { await containment.ExpireAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { failures.Report("containment-expiry", e); }
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            var today = DateOnly.FromDateTime(local.DateTime);
            if (o.CsatReviewEnabled && csatDoneFor != today && TimeOnly.FromDateTime(local.DateTime) >= csatAt)
            {
                try { await csat.RunAsync(stoppingToken); csatDoneFor = today; }
                catch (Exception e) when (e is not OperationCanceledException) { failures.Report("csat-review", e); }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
