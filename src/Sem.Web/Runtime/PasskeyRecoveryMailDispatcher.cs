using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public interface IPasskeyRecoveryMailTransport
{
    Task Send(string id, PasskeyRecoveryMailPayload payload, CancellationToken ct);
}

public sealed class PasskeyRecoveryMailTransport(NotificationService notifications)
    : IPasskeyRecoveryMailTransport
{
    public Task Send(string id, PasskeyRecoveryMailPayload payload, CancellationToken ct) =>
        notifications.SendEmailTo(payload.Recipient, payload.Subject, payload.Body, ct,
            SemEndpoints.Hash("passkey-recovery:" + id) + "@notifications.sem.invalid");
}

public sealed class PasskeyRecoveryMailDispatcher(
    IDbContextFactory<ApplicationDbContext> factory,
    IDataProtectionProvider protection,
    IPasskeyRecoveryMailTransport transport,
    TimeProvider clock,
    ILogger<PasskeyRecoveryMailDispatcher> logger)
{
    readonly IDataProtector mailProtector = protection.CreateProtector("SEM.PasskeyRecovery.Mail.v1");

    public async Task DispatchOnce(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        await db.PasskeyRecoveryMails.Where(x => x.Status == "Sending" &&
            x.LeaseUntil <= now && x.Attempts >= 5)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Failed")
                .SetProperty(x => x.Claim, (string?)null).SetProperty(x => x.LeaseUntil, (long?)null)
                .SetProperty(x => x.ProtectedPayload, "").SetProperty(x => x.Error, "LeaseExpired"), ct);
        var ids = await db.PasskeyRecoveryMails.AsNoTracking()
            .Where(x => x.Attempts < 5 && x.Due <= now &&
                (x.Status == "Pending" || x.Status == "Sending" && x.LeaseUntil <= now))
            .OrderBy(x => x.Due).Select(x => x.Id).Take(10).ToListAsync(ct);

        foreach (var id in ids)
        {
            now = clock.GetUtcNow().ToUnixTimeSeconds();
            var claim = Guid.NewGuid().ToString("N");
            var acquired = await db.PasskeyRecoveryMails
                .Where(x => x.Id == id && x.Attempts < 5 && x.Due <= now &&
                    (x.Status == "Pending" || x.Status == "Sending" && x.LeaseUntil <= now))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Sending")
                    .SetProperty(x => x.Claim, claim).SetProperty(x => x.LeaseUntil, now + 120)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (acquired != 1) continue;

            var item = await db.PasskeyRecoveryMails.AsNoTracking()
                .SingleAsync(x => x.Id == id && x.Claim == claim, ct);
            try
            {
                var operation = await db.PasskeyRecoveryOperations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == item.OperationId && x.UserId == item.UserId, ct);
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.UserId, ct);
                var active = operation is not null && user is not null &&
                    IsActive(item, operation, user, now);
                if (!active)
                {
                    await Finish(db, id, claim, "Cancelled", null, ct);
                    continue;
                }
                var payload = JsonSerializer.Deserialize<PasskeyRecoveryMailPayload>(
                    mailProtector.Unprotect(item.ProtectedPayload));
                if (payload is null) throw new InvalidOperationException("Invalid protected mail payload.");
                await transport.Send(id, payload, ct);
                await Finish(db, id, claim, "Sent", clock.GetUtcNow().ToUnixTimeSeconds(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                var terminal = item.Attempts >= 5;
                await db.PasskeyRecoveryMails
                    .Where(x => x.Id == id && x.Status == "Sending" && x.Claim == claim)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, terminal ? "Failed" : "Pending")
                        .SetProperty(x => x.Due, clock.GetUtcNow().ToUnixTimeSeconds() +
                            (long)Math.Pow(2, item.Attempts) * 15)
                        .SetProperty(x => x.Claim, (string?)null)
                        .SetProperty(x => x.LeaseUntil, (long?)null)
                        .SetProperty(x => x.Error, e.GetType().Name)
                        .SetProperty(x => x.ProtectedPayload, terminal ? "" : item.ProtectedPayload), ct);
                // Never log recipient, body, link, token, or proof hash.
                logger.LogWarning("Passkey-recovery email dispatch failed; attempt={Attempt}, type={Type}",
                    item.Attempts, e.GetType().Name);
            }
        }
    }

    static bool IsActive(PasskeyRecoveryMail item,
        PasskeyRecoveryOperation operation, ApplicationUser user, long now)
    {
        if (!user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email) ||
            operation.EmailHash != SemEndpoints.Hash(user.NormalizedEmail ?? ""))
            return false;
        if (item.Kind == "RecoveryLink")
            return item.ExpiresAt > now && operation.ExpiresAt > now &&
                operation.Status == "Pending" && operation.DisabledForRecovery && !user.Enabled &&
                operation.SecurityStamp == user.SecurityStamp && item.ProofHash is not null &&
                item.ProofHash == operation.TokenHash;
        if (item.Kind == "RecoveryComplete")
            return item.ExpiresAt > now && operation.Status == "Completed" &&
                !operation.DisabledForRecovery && user.Enabled;
        return false;
    }

    static Task<int> Finish(ApplicationDbContext db, string id, string claim,
        string status, long? sentAt, CancellationToken ct) =>
        db.PasskeyRecoveryMails.Where(x => x.Id == id && x.Status == "Sending" && x.Claim == claim)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status)
                .SetProperty(x => x.SentAt, sentAt)
                .SetProperty(x => x.Claim, (string?)null)
                .SetProperty(x => x.LeaseUntil, (long?)null)
                .SetProperty(x => x.ProtectedPayload, ""), ct);
}

public sealed class PasskeyRecoveryMailWorker(
    IServiceScopeFactory scopes,
    ILogger<PasskeyRecoveryMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PasskeyRecoveryMailDispatcher>()
                    .DispatchOnce(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                logger.LogWarning("Passkey-recovery mail iteration failed ({Type})", e.GetType().Name);
            }
        }
    }
}
