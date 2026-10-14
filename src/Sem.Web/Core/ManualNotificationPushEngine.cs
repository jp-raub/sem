using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Core;

public sealed record ManualNotificationPushBatchResult(int PreparedRecipients, int ProcessedDeliveries, int SweptDeliveries, int FailedItems);
// Transient fairness hint only; never authorization, ownership or persisted audience.
public sealed class ManualNotificationPushScanState
{
    internal sealed record Position(long Epoch, long AcceptedAt, string RecipientId);
    private Position? position;
    internal Position? Read() => Volatile.Read(ref position);
    internal void Set(Position? value) => Volatile.Write(ref position, value);
}
// Bounded orchestration composed by AddManualNotificationPushPipeline; activation remains a durable gate.
public sealed class ManualNotificationPushEngine(IDbContextFactory<ApplicationDbContext> factory,
    ManualNotificationPushQueue queue, ManualNotificationPushDispatcher dispatcher, TimeProvider clock)
{
    public const int BatchSize = 20;
    private readonly ManualNotificationPushScanState localScan = new();
    public async Task<ManualNotificationPushBatchResult> RunOnceAsync(CancellationToken ct = default, ManualNotificationPushScanState? scanState = null)
    {
        ct.ThrowIfCancellationRequested();
        var swept = await queue.SweepAsync(ct);
        string[] recipients;
        await using (var db = await factory.CreateDbContextAsync(ct)) {
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            var activation = await db.Set<ManualNotificationPushActivation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
            var state = scanState ?? localScan;
            var cursor = state.Read();
            if (activation is not { Enabled: true }) { recipients = []; state.Set(null); }
            else {
                if (cursor?.Epoch != activation.Epoch) cursor = null;
                var candidates = from r in db.Set<ManualNotificationRecipient>().AsNoTracking()
                    join a in db.Set<ManualNotificationPushAdmission>().AsNoTracking() on r.OperationId equals a.OperationId
                    where a.Epoch == activation.Epoch && a.AcceptedAt <= now && a.ExpiresAt > now
                        && !db.Set<ManualNotificationPushPreparation>().Any(x => x.RecipientId == r.Id)
                    select new { r.Id, a.AcceptedAt };
                var next = candidates;
                if (cursor is not null) {
                    var at = cursor.AcceptedAt; var id = cursor.RecipientId;
                    next = next.Where(x => x.AcceptedAt > at || x.AcceptedAt == at && x.Id.CompareTo(id) > 0);
                }
                var selected = await next.OrderBy(x => x.AcceptedAt).ThenBy(x => x.Id).Take(BatchSize).ToListAsync(ct);
                if (cursor is not null && selected.Count < BatchSize) {
                    var at = cursor.AcceptedAt; var id = cursor.RecipientId;
                    var wrap = await candidates.Where(x => x.AcceptedAt < at || x.AcceptedAt == at && x.Id.CompareTo(id) <= 0)
                        .OrderBy(x => x.AcceptedAt).ThenBy(x => x.Id).Take(BatchSize - selected.Count).ToListAsync(ct);
                    selected.AddRange(wrap);
                }
                recipients = selected.Select(x => x.Id).ToArray();
                // Advance even past failed items; a poisoned first batch cannot monopolize later passes.
                // Queue transactions/fencing, not this hint, protect concurrent/restarted execution.
                if (selected.Count > 0) {
                    var last = selected[^1]; state.Set(new(activation.Epoch, last.AcceptedAt, last.Id));
                } else state.Set(null);
            }
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
// Hosted worker uses a fresh scope per pass and preserves only a transient fairness hint.
public sealed class ManualNotificationPushWorker(IServiceScopeFactory scopes, ILogger<ManualNotificationPushWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        var scanState = new ManualNotificationPushScanState(); // survives per-pass scope disposal
        try {
            while (await timer.WaitForNextTickAsync(ct)) {
                try {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<ManualNotificationPushEngine>().RunOnceAsync(ct, scanState);
                    if (result.FailedItems > 0) logger.LogWarning("Manual Push batch skipped {Count} failed items", result.FailedItems);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning("Manual Push batch failed; type={Type}", ex.GetType().Name); }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
