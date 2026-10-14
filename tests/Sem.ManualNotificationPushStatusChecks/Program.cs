using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-manual-push-status-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var path = Path.Combine(root, "status.db");
var clock = new TestClock(DateTimeOffset.Parse("2026-10-14T09:00:00Z"));
var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + path), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<ApplicationDbContext>();
services.AddDataProtection();
services.AddSingleton<TimeProvider>(clock);
services.AddSingleton<BusinessNotificationStore>();
services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
async Task<T> Throws<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); }
    catch (T e) { checks++; Console.WriteLine("PASS " + label); return e; }
    throw new Exception("FAIL " + label + ": expected " + typeof(T).Name);
}
ClaimsPrincipal Principal(string id, string? stamp = null, string? session = null) => new(
    new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, id),
        new Claim("AspNet.Identity.SecurityStamp", stamp ?? id + "-stamp"),
        new Claim(PasskeyAdditionService.SessionClaim, session ?? id + "-session")
    ], "Identity.Application"));
var manager = Principal("manager");
var otherManager = Principal("other-manager");
var globalAdmin = Principal("global-admin");
var member = Principal("member-1");
async Task Change(Func<ApplicationDbContext, Task> update)
{
    await using var db = await factory.CreateDbContextAsync();
    await update(db);
    await db.SaveChangesAsync();
}
async Task<string> Accept(string subject)
{
    var preview = await core.PreviewEventManualNotificationAsync(manager, "event", subject, "PRIVATE-BODY");
    var id = Guid.NewGuid().ToString("D");
    var accepted = await core.AcceptEventManualNotificationAsync(manager, "event",
        new(id, preview.Fingerprint, preview.Subject, preview.Body, true));
    Check(accepted.RecipientCount == 4, "acceptance fixture captures four original recipients");
    return id;
}
string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
try
{
    await using (var db = await factory.CreateDbContextAsync())
        await db.GetService<IMigrator>().MigrateAsync("20261014081050_AddManualNotificationPushActivationControl");
    await using (var db = await factory.CreateDbContextAsync())
    {
        foreach (var id in new[] { "manager", "other-manager", "global-admin", "member-1", "member-2", "member-3", "member-4", "late-member" })
            db.Users.Add(new ApplicationUser { Id = id, UserName = id, Email = id + "@PRIVATE.invalid", DisplayName = id,
                Enabled = true, EmailConfirmed = true, SecurityStamp = id + "-stamp", IsAdmin = id == "global-admin" });
        var e = new SessionEvent { Id = "event", Title = "PRIVATE-EVENT", ScheduledDate = "2099-01-01T00:00:00Z",
            CreatedAt = "2026-10-01T00:00:00Z", PhaseKind = "View", ManualPhaseOverride = true };
        e.Phases.Add(new EventPhase { Id = "view", EventId = "event", Kind = "View", Name = "View", IsActive = true });
        e.Phases.Add(new EventPhase { Id = "hidden", EventId = "event", Kind = "Hidden", Name = "Hidden" });
        e.Admins.Add(new EventAdmin { EventId = "event", UserId = "manager", GrantedAt = "2026-10-01T00:00:00Z" });
        e.Admins.Add(new EventAdmin { EventId = "event", UserId = "other-manager", GrantedAt = "2026-10-01T00:00:00Z" });
        db.SessionEvents.Add(e);
        foreach (var n in Enumerable.Range(1, 4)) db.EventSpectators.Add(new EventSpectator {
            EventId = "event", UserId = "member-" + n, RegisteredBy = "manager", RegisteredAt = "2026-10-01T00:00:00Z" });
        var otherEvent = new SessionEvent { Id = "other-event", Title = "PRIVATE-OTHER-EVENT", ScheduledDate = "2099-01-01T00:00:00Z",
            CreatedAt = "2026-10-01T00:00:00Z", PhaseKind = "View", ManualPhaseOverride = true };
        otherEvent.Phases.Add(new EventPhase { Id = "other-view", EventId = "other-event", Kind = "View", Name = "View", IsActive = true });
        otherEvent.Admins.Add(new EventAdmin { EventId = "other-event", UserId = "manager", GrantedAt = "2026-10-01T00:00:00Z" });
        db.SessionEvents.Add(otherEvent);
        db.EventSpectators.Add(new EventSpectator { EventId = "other-event", UserId = "member-1", RegisteredBy = "manager", RegisteredAt = "2026-10-01T00:00:00Z" });
        await db.SaveChangesAsync();
    }

    // Accepted while activation is OFF: no admission is ever backfilled by a later ON state.
    var offId = await Accept("PRIVATE-OFF-SUBJECT");
    await Change(db => { db.Add(new ManualNotificationPushActivation { Enabled = true, Epoch = 1 }); return Task.CompletedTask; });
    var offStatus = await core.ReadEventManualNotificationPushStatusAsync(manager, "event", offId);
    Check(offStatus.AdmissionGate == "NotAdmitted" && offStatus.ExpiresAt is null, "OFF acceptance remains not admitted after activation turns ON");

    // ON acceptance with mixed recipient preparation and every persisted delivery state.
    var requestId = await Accept("PRIVATE-STATUS-SUBJECT");
    string[] recipientIds;
    await using (var db = await factory.CreateDbContextAsync())
        recipientIds = await db.Set<ManualNotificationRecipient>().AsNoTracking().Where(x => x.OperationId == requestId)
            .OrderBy(x => x.UserId).Select(x => x.Id).ToArrayAsync();
    var recipientByUser = new Dictionary<string, string>();
    await using (var db = await factory.CreateDbContextAsync())
        recipientByUser = await db.Set<ManualNotificationRecipient>().AsNoTracking().Where(x => x.OperationId == requestId)
            .ToDictionaryAsync(x => x.UserId, x => x.Id);
    var preparedRecipient = recipientByUser["member-1"];
    await using (var db = await factory.CreateDbContextAsync())
    {
        db.Add(new ManualNotificationPushPreparation { RecipientId = preparedRecipient, Outcome = "Prepared", DeviceCount = 7, PreparedAt = clock.GetUtcNow().ToUnixTimeSeconds() });
        db.Add(new ManualNotificationPushPreparation { RecipientId = recipientByUser["member-2"], Outcome = "NoEligibleDevices", DeviceCount = 0, PreparedAt = clock.GetUtcNow().ToUnixTimeSeconds() });
        db.Add(new ManualNotificationPushPreparation { RecipientId = recipientByUser["member-4"], Outcome = "ViewingDenied", DeviceCount = 0, PreparedAt = clock.GetUtcNow().ToUnixTimeSeconds() });
        var states = new[] { "Pending", "Claimed", "Accepted", "Unknown", "Expired", "Suppressed", "Failed" };
        for (var i = 0; i < states.Length; i++)
        {
            var state = states[i];
            db.Add(new ManualNotificationPushDelivery {
                Id = Digest("delivery-" + i), RecipientId = preparedRecipient, DeviceId = "PRIVATE-DEVICE-" + i,
                BindingFingerprint = Digest("private-binding-" + i), ExpiresAt = clock.GetUtcNow().ToUnixTimeSeconds() + 600,
                DueAt = clock.GetUtcNow().ToUnixTimeSeconds(), State = state, Reason = "synthetic",
                Attempts = state == "Claimed" ? 1 : 0,
                Claim = state == "Claimed" ? "PRIVATE-CLAIM" : null,
                LeaseUntil = state == "Claimed" ? clock.GetUtcNow().ToUnixTimeSeconds() + 30 : null,
                ProviderAcceptedAt = state == "Accepted" ? clock.GetUtcNow().ToUnixTimeSeconds() : null
            });
        }
        await db.SaveChangesAsync();
    }
    // Registration changes after preparation do not rewrite the persisted status snapshot.
    await Change(async db => {
        db.EventSpectators.Remove(await db.EventSpectators.SingleAsync(x => x.EventId == "event" && x.UserId == "member-1"));
        db.EventSpectators.Add(new EventSpectator { EventId = "event", UserId = "late-member", RegisteredBy = "manager", RegisteredAt = "2026-10-14T09:00:00Z" });
        await Task.CompletedTask;
    });
    var status = await core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId);
    Check(status.AdmissionGate == "Eligible" && status.ExpiresAt == status.AcceptedAt + 600, "current admission gate and original expiry are reported");
    Check(status.RecipientCount == 4 && status.PreparedRecipients == 1 && status.UnpreparedRecipients == 1 &&
          status.NoEligibleDevices == 1 && status.OtherSuppressedPreparations == 1, "recipient preparation counts remain separate and sum to snapshot");
    Check(status.PersistedDeliveryCounts == new ManualNotificationPushDeliveryCounts(1, 1, 1, 1, 1, 1, 1),
        "persisted delivery counts report each state, independent of current registration");
    Check(status.PreparedRecipients != status.PersistedDeliveryCounts.Pending + status.PersistedDeliveryCounts.Claimed +
          status.PersistedDeliveryCounts.Accepted + status.PersistedDeliveryCounts.Unknown + status.PersistedDeliveryCounts.Expired +
          status.PersistedDeliveryCounts.Suppressed + status.PersistedDeliveryCounts.Failed,
        "recipient counts are not conflated with per-device delivery counts");

    // Aggregate-only serialization: no content, audience identities, or device/private binding data.
    var json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using (var doc = JsonDocument.Parse(json))
    {
        var rootKeys = doc.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray();
        Check(rootKeys.SequenceEqual(new[] { "acceptedAt", "admissionGate", "eventId", "expiresAt", "noEligibleDevices",
            "otherSuppressedPreparations", "persistedDeliveryCounts", "preparedRecipients", "recipientCount", "requestId", "unpreparedRecipients" }.Order()),
            "serialized DTO contains only aggregate contract properties");
    }
    foreach (var secret in new[] { "PRIVATE-STATUS-SUBJECT", "PRIVATE-BODY", "member-1", "manager", "PRIVATE-DEVICE", "PRIVATE-CLAIM", "private-binding", "PRIVATE.invalid" })
        Check(!json.Contains(secret, StringComparison.Ordinal), "serialized DTO excludes " + secret);

    // Transactional read leaves all durable inbox/outbox/audit rows unchanged.
    async Task<string> Snapshot()
    {
        await using var db = await factory.CreateDbContextAsync();
        var ops = await db.Set<ManualNotificationOperation>().AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.RecipientCount, x.AcceptedAt }).ToArrayAsync();
        var recipients = await db.Set<ManualNotificationRecipient>().AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.OperationId, x.PushState }).ToArrayAsync();
        var preps = await db.Set<ManualNotificationPushPreparation>().AsNoTracking().OrderBy(x => x.RecipientId)
            .Select(x => new { x.RecipientId, x.Outcome, x.DeviceCount }).ToArrayAsync();
        var deliveries = await db.Set<ManualNotificationPushDelivery>().AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.State, x.Attempts, x.Reason }).ToArrayAsync();
        var messages = await db.Messages.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.UserId, x.Title, x.Body, x.ReadAt }).ToArrayAsync();
        var audits = await db.Audits.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Action, x.EntityId }).ToArrayAsync();
        var admissions = await db.Set<ManualNotificationPushAdmission>().AsNoTracking().OrderBy(x => x.OperationId)
            .Select(x => new { x.OperationId, x.AcceptedAt, x.ExpiresAt }).ToArrayAsync();
        return JsonSerializer.Serialize(new { ops, recipients, preps, deliveries, messages, audits, admissions });
    }
    var beforeRead = await Snapshot();
    _ = await core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId);
    Check(beforeRead == await Snapshot(), "read changes no operation, recipient, preparation, delivery, inbox, admission, or audit rows");

    // An ID owned by the same actor but bound to a different event is still not interchangeable.
    var otherPreview = await core.PreviewEventManualNotificationAsync(manager, "other-event", "PRIVATE-CROSS-EVENT", "PRIVATE-BODY");
    var otherEventRequestId = Guid.NewGuid().ToString("D");
    await core.AcceptEventManualNotificationAsync(manager, "other-event",
        new(otherEventRequestId, otherPreview.Fingerprint, otherPreview.Subject, otherPreview.Body, true));
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", otherEventRequestId),
        "operation ID bound to a different event is denied");

    // Ownership is exact even for another manager or a global administrator.
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(otherManager, "event", requestId), "other event manager cannot read author's operation");
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(globalAdmin, "event", requestId), "global administrator cannot read another author's operation");
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(member, "event", requestId), "ordinary event member cannot read operation status");
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "missing-event", requestId), "wrong event is denied");
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", Guid.NewGuid().ToString("D")), "unknown operation ID is denied");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId.ToUpperInvariant()), "uppercase request ID is rejected");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", "not-a-guid"), "malformed request ID is rejected");
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(Principal("manager", "wrong-stamp"), "event", requestId), "stale security stamp is denied");
    await Change(db => { db.RevokedAuthSessions.Add(new RevokedAuthSession { UserId = "manager", TokenHash = SemEndpoints.Hash("manager-session"), RevokedAt = "2026-10-14T09:00:00Z" }); return Task.CompletedTask; });
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId), "revoked session is denied");
    await Change(async db => { db.RevokedAuthSessions.Remove(await db.RevokedAuthSessions.SingleAsync()); });
    await Change(async db => { (await db.Users.SingleAsync(x => x.Id == "manager")).Enabled = false; });
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId), "disabled operation author is denied");
    await Change(async db => { (await db.Users.SingleAsync(x => x.Id == "manager")).Enabled = true; });
    await Change(async db => { db.EventAdmins.Remove(await db.EventAdmins.SingleAsync(x => x.EventId == "event" && x.UserId == "manager")); });
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId), "removed manager authority is denied");
    await Change(db => { db.EventAdmins.Add(new EventAdmin { EventId = "event", UserId = "manager", GrantedAt = "2026-10-14T09:00:00Z" }); return Task.CompletedTask; });
    await Change(async db => { (await db.Phases.SingleAsync(x => x.Id == "view")).IsActive = false; });
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId), "invalid current effective event view is denied");
    await Change(async db => { (await db.Phases.SingleAsync(x => x.Id == "view")).IsActive = true; });

    // Current activation OFF or rotated to a different generation stops an admitted operation; epoch is never returned.
    await Change(async db => { (await db.Set<ManualNotificationPushActivation>().SingleAsync()).Enabled = false; });
    Check((await core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId)).AdmissionGate == "Stopped", "current global OFF stops old admitted operation");
    await Change(async db => { var a = await db.Set<ManualNotificationPushActivation>().SingleAsync(); a.Enabled = true; a.Epoch = 2; });
    Check((await core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId)).AdmissionGate == "Stopped", "new activation epoch does not revive old admitted operation");
    var expiresAt = status.ExpiresAt!.Value;
    clock.Now = DateTimeOffset.FromUnixTimeSeconds(expiresAt);
    await Change(async db => { var a = await db.Set<ManualNotificationPushActivation>().SingleAsync(); a.Enabled = true; a.Epoch = 1; });
    Check((await core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId)).AdmissionGate == "Expired", "expiry boundary is exclusive at expiresAt");
    clock.Now = DateTimeOffset.Parse("2026-10-14T09:00:00Z");

    // A recipient with no preparation has zero persisted device rows and remains unprepared.
    var zeroId = await Accept("PRIVATE-ZERO-SUBJECT");
    var zero = await core.ReadEventManualNotificationPushStatusAsync(manager, "event", zeroId);
    Check(zero.UnpreparedRecipients == 4 && zero.PersistedDeliveryCounts == new ManualNotificationPushDeliveryCounts(0, 0, 0, 0, 0, 0, 0),
        "zero persisted device statuses are represented as zero without inventing eligibility");

    // Unknown preparation outcomes are rejected rather than silently counted as suppression.
    await Change(async db => { var p = await db.Set<ManualNotificationPushPreparation>().SingleAsync(x => x.RecipientId == preparedRecipient); p.Outcome = "FutureUnknownState"; p.DeviceCount = 0; });
    await Throws<InvalidOperationException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId), "unknown stored preparation state is rejected explicitly");
    await Change(async db => { var p = await db.Set<ManualNotificationPushPreparation>().SingleAsync(x => x.RecipientId == preparedRecipient); p.Outcome = "Prepared"; p.DeviceCount = 7; });
    var corruptDelivery = Digest("delivery-0");
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA ignore_check_constraints=ON";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "UPDATE ManualNotificationPushDeliveries SET State='FutureUnknownState' WHERE Id='" + corruptDelivery + "'";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "PRAGMA ignore_check_constraints=OFF";
        await command.ExecuteNonQueryAsync();
    }
    await Throws<InvalidOperationException>(() => core.ReadEventManualNotificationPushStatusAsync(manager, "event", requestId),
        "unknown stored delivery state is rejected explicitly");
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA ignore_check_constraints=ON";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "UPDATE ManualNotificationPushDeliveries SET State='Pending' WHERE Id='" + corruptDelivery + "'";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "PRAGMA ignore_check_constraints=OFF";
        await command.ExecuteNonQueryAsync();
    }

    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        Check((string?)await command.ExecuteScalarAsync() == "ok", "SQLite integrity_check passes");
        command.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await command.ExecuteReaderAsync();
        Check(!await reader.ReadAsync(), "SQLite foreign_key_check returns zero rows");
    }
    Console.WriteLine($"Manual notification Push status: {checks} synthetic checks passed.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, true); } catch { }
}

sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
