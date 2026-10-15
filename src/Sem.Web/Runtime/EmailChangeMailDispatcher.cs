using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Runtime;

public interface IEmailChangeMailTransport { Task Send(string id, EmailChangeMailPayload payload, CancellationToken ct); }
public sealed class EmailChangeMailTransport(NotificationService notifications) : IEmailChangeMailTransport
{
    public Task Send(string id, EmailChangeMailPayload payload, CancellationToken ct) =>
        notifications.SendEmailTo(payload.Recipient, payload.Subject, payload.Body, ct,
            SemEndpoints.Hash("email-change:" + id) + "@notifications.sem.invalid");
}
public sealed class EmailChangeMailDispatcher(IDbContextFactory<ApplicationDbContext> factory,
    IDataProtectionProvider protection, IEmailChangeMailTransport transport, TimeProvider clock,
    ILogger<EmailChangeMailDispatcher> logger)
{
    readonly IDataProtector protector = protection.CreateProtector("SEM.EmailChange.Mail.v1");
    public async Task DispatchOnce(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        await db.EmailChangeMails.Where(x => x.Status == "Sending" && x.LeaseUntil <= now && x.Attempts >= 5)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Failed").SetProperty(x => x.Claim, (string?)null)
                .SetProperty(x => x.LeaseUntil, (long?)null).SetProperty(x => x.ProtectedPayload, "").SetProperty(x => x.Error, "LeaseExpired"), ct);
        var ids = await db.EmailChangeMails.AsNoTracking().Where(x => x.Attempts < 5 && x.Due <= now &&
            (x.Status == "Pending" || x.Status == "Sending" && x.LeaseUntil <= now)).OrderBy(x => x.Due).Select(x => x.Id).Take(10).ToListAsync(ct);
        foreach (var id in ids)
        {
            now = clock.GetUtcNow().ToUnixTimeSeconds(); var claim = Guid.NewGuid().ToString();
            var acquired = await db.EmailChangeMails.Where(x => x.Id == id && x.Attempts < 5 && x.Due <= now &&
                (x.Status == "Pending" || x.Status == "Sending" && x.LeaseUntil <= now))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Sending").SetProperty(x => x.Claim, claim)
                    .SetProperty(x => x.LeaseUntil, now + 120).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (acquired != 1) continue;
            var item = await db.EmailChangeMails.AsNoTracking().SingleAsync(x => x.Id == id && x.Claim == claim, ct);
            try
            {
                var active = item.ExpiresAt > now && await db.Users.AnyAsync(x => x.Id == item.UserId && x.Enabled && x.EmailConfirmed, ct);
                if (item.Kind == "Verification")
                {
                    var op = await db.EmailChangeOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.OperationId, ct);
                    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.UserId, ct);
                    active &= op is not null && user is not null && op.Status == "Pending" && op.ExpiresAt > now &&
                        op.SecurityStamp == user.SecurityStamp && op.OriginalEmailHash == SemEndpoints.Hash(user.NormalizedEmail ?? "");
                }
                if (!active) { await Finish(db, id, claim, "Cancelled", null, ct); continue; }
                var payload = JsonSerializer.Deserialize<EmailChangeMailPayload>(protector.Unprotect(item.ProtectedPayload))!;
                await transport.Send(id, payload, ct);
                await Finish(db, id, claim, "Sent", clock.GetUtcNow().ToUnixTimeSeconds(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                var terminal = item.Attempts >= 5;
                await db.EmailChangeMails.Where(x => x.Id == id && x.Status == "Sending" && x.Claim == claim)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, terminal ? "Failed" : "Pending")
                        .SetProperty(x => x.Due, clock.GetUtcNow().ToUnixTimeSeconds() + (long)Math.Pow(2, item.Attempts) * 15)
                        .SetProperty(x => x.Claim, (string?)null).SetProperty(x => x.LeaseUntil, (long?)null)
                        .SetProperty(x => x.Error, e.GetType().Name).SetProperty(x => x.ProtectedPayload, terminal ? "" : item.ProtectedPayload), ct);
                logger.LogWarning("Email-change mail failed; attempt={Attempt}, type={Type}", item.Attempts, e.GetType().Name);
            }
        }
    }
    static Task<int> Finish(ApplicationDbContext db, string id, string claim, string status, long? sent, CancellationToken ct) =>
        db.EmailChangeMails.Where(x => x.Id == id && x.Status == "Sending" && x.Claim == claim)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.SentAt, sent)
                .SetProperty(x => x.Claim, (string?)null).SetProperty(x => x.LeaseUntil, (long?)null).SetProperty(x => x.ProtectedPayload, ""), ct);
}
public sealed class EmailChangeMailWorker(IServiceScopeFactory scopes, ILogger<EmailChangeMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<EmailChangeMailDispatcher>().DispatchOnce(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Email-change mail iteration failed ({Type})", e.GetType().Name); }
        }
    }
}
