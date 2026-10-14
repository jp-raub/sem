using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Core;

public sealed record ManualNotificationPushBatchResult(int PreparedRecipients, int ProcessedDeliveries, int SweptDeliveries, int FailedItems);
// Bounded orchestration only. Not registered in DI/HTTP/hosted services.
public sealed class ManualNotificationPushEngine(IDbContextFactory<ApplicationDbContext> factory,
    ManualNotificationPushQueue queue, ManualNotificationPushDispatcher dispatcher, TimeProvider clock)
{
    public const int BatchSize = 20;
    public async Task<ManualNotificationPushBatchResult> RunOnceAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var swept = await queue.SweepAsync(ct);
        string[] recipients;
        await using (var db = await factory.CreateDbContextAsync(ct)) {
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            var activation = await db.Set<ManualNotificationPushActivation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
            recipients = activation is not { Enabled: true } ? [] : await (
                from r in db.Set<ManualNotificationRecipient>().AsNoTracking()
                join a in db.Set<ManualNotificationPushAdmission>().AsNoTracking() on r.OperationId equals a.OperationId
                where a.Epoch == activation.Epoch && a.AcceptedAt <= now && a.ExpiresAt > now
                    && !db.Set<ManualNotificationPushPreparation>().Any(x => x.RecipientId == r.Id)
                orderby a.AcceptedAt, r.Id
                select r.Id).Take(BatchSize).ToArrayAsync(ct);
        }
        int prepared = 0, processed = 0, failed = 0;
        foreach (var recipient in recipients) {
            ct.ThrowIfCancellationRequested();
            try { await queue.PrepareAsync(recipient, ct);prepared++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { failed++; } // continue independent items; return a bounded count, no secrets
        }
        string[] deliveries;
        await using (var db = await factory.CreateDbContextAsync(ct)) {
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            deliveries = await db.Set<ManualNotificationPushDelivery>().AsNoTracking()
                .Where(x => x.State == "Pending" && x.DueAt <= now && x.ExpiresAt > now)
                .OrderBy(x => x.DueAt).ThenBy(x => x.Id).Select(x => x.Id).Take(BatchSize).ToArrayAsync(ct);
        }
        foreach (var delivery in deliveries) {
            ct.ThrowIfCancellationRequested();
            try { await dispatcher.DispatchOneAsync(delivery, ct);processed++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { failed++; }
        }
        // Counts indicate processed calls, NEVER handset receipt/provider acceptance.
        return new(prepared, processed, swept, failed);
    }
}
// Defined for later explicit composition, but deliberately NOT AddHostedService'd.
public sealed class ManualNotificationPushWorker(IServiceScopeFactory scopes, ILogger<ManualNotificationPushWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try {
            while (await timer.WaitForNextTickAsync(ct)) {
                try {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<ManualNotificationPushEngine>().RunOnceAsync(ct);
                    if (result.FailedItems > 0) logger.LogWarning("Manual Push batch skipped {Count} failed items", result.FailedItems);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning("Manual Push batch failed; type={Type}", ex.GetType().Name); }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
