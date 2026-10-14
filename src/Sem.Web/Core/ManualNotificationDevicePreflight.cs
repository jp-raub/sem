using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

// Facts for a future dispatcher, not a send authorization or an HTTP response.
// No worker/DI registration/transport invocation. A passing binding still lacks approved dispatch policy.
public sealed record ManualNotificationDevicePreflightResult(string State, bool? CurrentlyRegistered = null)
{
    public bool DispatchConnected => false;
}
public sealed class ManualNotificationDevicePreflight(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
{
    public async Task<ManualNotificationDevicePreflightResult> ReadAsync(string recipientId, string deviceId,
        string expectedBindingFingerprint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recipientId) || string.IsNullOrWhiteSpace(deviceId) ||
            expectedBindingFingerprint is null || expectedBindingFingerprint.Length != 64 ||
            expectedBindingFingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            return new("InvalidInput");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Start at an immutable, accepted snapshot; never discover additional recipients.
        var snapshot = await db.Set<ManualNotificationRecipient>().AsNoTracking()
            .Where(x => x.Id == recipientId).Select(x => new { x.Id, x.EventId, x.OperationId, x.UserId }).SingleOrDefaultAsync(ct);
        if (snapshot is null || !await db.Set<ManualNotificationOperation>().AsNoTracking()
            .AnyAsync(x => x.Id == snapshot.OperationId && x.EventId == snapshot.EventId, ct))
            return new("MissingAcceptedRecipient");
        if (!await db.Messages.AsNoTracking().AnyAsync(x => x.Id == snapshot.Id && x.UserId == snapshot.UserId, ct))
            return new("MissingInbox");
        // Authentication email/profile/answer/message content is not projected into this checker or its result.
        var user = await db.Users.AsNoTracking().Where(x => x.Id == snapshot.UserId)
            .Select(x => new ApplicationUser { Id = x.Id, Enabled = x.Enabled, EmailConfirmed = x.EmailConfirmed,
                IsAdmin = x.IsAdmin, SecurityStamp = x.SecurityStamp }).SingleOrDefaultAsync(ct);
        if (user is null || !user.Enabled || !user.EmailConfirmed) return new("AccountUnavailable");
        var e = await db.SessionEvents.AsNoTracking().Include(x => x.Admins).Include(x => x.Phases)
            .SingleOrDefaultAsync(x => x.Id == snapshot.EventId, ct);
        if (e is null || !CoreFoundationService.PushViewAllowed(e, user, clock.GetUtcNow())) return new("ViewingDenied");
        var registered = await db.EventSpectators.AsNoTracking().AnyAsync(x => x.EventId == snapshot.EventId && x.UserId == user.Id, ct)
            || await (from part in db.SongParts.AsNoTracking()
                      join song in db.Songs.AsNoTracking() on part.SongId equals song.Id
                      join definition in db.EventPartDefinitions.AsNoTracking() on part.PartDefinitionId equals definition.Id
                      where part.EventId == snapshot.EventId && song.EventId == snapshot.EventId && definition.EventId == snapshot.EventId
                          && song.DeletedAt == null && definition.DeletedAt == null && part.AssignedUserId == user.Id
                      select part.Id).AnyAsync(ct);
        var device = await db.PushDevices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId && x.UserId == user.Id, ct);
        if (device is null) return new("DeviceUnavailable", registered);
        if (!await PushBinding.Valid(db, user, device, ct)) return new("BindingInvalid", registered);
        if (PushBinding.Fingerprint(device) != expectedBindingFingerprint) return new("BindingChanged", registered);
        await tx.CommitAsync(ct);
        // Registration is a fact, not a silently chosen post-acceptance removal/retention policy.
        // No deadline, TTL, dispatch lease, device delivery row or payload is created here.
        return new("PolicyPending", registered);
    }
}
