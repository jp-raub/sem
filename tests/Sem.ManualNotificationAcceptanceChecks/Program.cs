using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var resume = args.Length == 3 && args[0] == "--resume";
var root = resume ? args[1] : Path.Combine(Path.GetTempPath(), "sem-manual-acceptance-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + Path.Combine(root, "data.db")), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton(TimeProvider.System);
DataProtectionServiceCollectionExtensions.AddDataProtection(services);
services.AddSingleton<BusinessNotificationStore>();
services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
async Task Deny<T>(Func<Task> action, string label) where T : Exception {
    bool denied = false; try { await action(); } catch (T) { denied = true; } Check(denied, label);
}
ClaimsPrincipal Who(string id) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, id), new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim, id + "-session"), new Claim(ClaimTypes.Role, "Admin")
], "Identity.Application"));
Task<ManualNotificationPreview> Preview(string actor = "manager", string id = "event", string subject = " 件名 ", string body = " 本文 ") =>
    core.PreviewEventManualNotificationAsync(Who(actor), id, subject, body);
if (resume) {
    await using var db = await factory.CreateDbContextAsync();
    await SqliteStorageInitializer.InitializeAsync(db);
    var op = await db.Set<ManualNotificationOperation>().SingleAsync(x=>x.Id==args[2]);
    var recovered = await core.AcceptEventManualNotificationAsync(Who("manager"), "event",
        new(op.Id, op.PreviewFingerprint, op.Subject, op.Body, true));
    if (!recovered.Replayed || recovered.RecipientCount != 2 || await db.Messages.CountAsync()!=3 || await db.Set<ManualNotificationOperation>().CountAsync()!=1)
        throw new Exception("Separate process replay recovery failed");
    Console.WriteLine("Separate process replay recovery: PASS");
    return;
}
try {
    await using (var db = await factory.CreateDbContextAsync()) {
        // Build the exact previous schema and populate unrelated durable state before upgrade.
        await db.GetService<IMigrator>().MigrateAsync("20261013181107_LinkQrReceptions");
        db.Users.Add(new() { Id="legacy", Email="legacy@PRIVATE.invalid", UserName="legacy@PRIVATE.invalid", DisplayName="legacy", Enabled=true, EmailConfirmed=true, SecurityStamp="legacy-stamp" });
        db.Messages.Add(new() { Id="legacy-message", UserId="legacy", Title="old title", Body="old body", Created=123, ReadAt=124 });
        db.Deliveries.Add(new() { Id="legacy-delivery", UserId="legacy", Kind="Push", Subject="old subject", ProtectedBody="opaque-existing-payload", Due=987, Attempts=2 });
        db.ServiceSettings.Add(new() { Id=1, Name="Upgrade sentinel", Description="Preserved existing settings" });
        await db.SaveChangesAsync();
        await SqliteStorageInitializer.InitializeAsync(db);
        Check((await db.Database.GetAppliedMigrationsAsync()).Last().EndsWith("_AddManualNotificationAcceptance"), "Additive migration upgrades populated previous schema");
        Check((await db.Users.SingleAsync(x=>x.Id=="legacy")).SecurityStamp=="legacy-stamp" &&
            (await db.Messages.SingleAsync(x=>x.Id=="legacy-message")).ReadAt==124 &&
            (await db.Deliveries.SingleAsync(x=>x.Id=="legacy-delivery")).ProtectedBody=="opaque-existing-payload" &&
            (await db.ServiceSettings.SingleAsync()).Name=="Upgrade sentinel", "Upgrade preserves existing identity stamp inbox scheduled delivery and service settings");
        foreach (var id in new[] { "admin", "manager", "viewer", "performer", "spectator", "disabled", "unconfirmed", "otherperson", "answeronly", "deletedonly" })
            db.Users.Add(new() { Id = id, Email = id + "@PRIVATE.invalid", UserName = id + "@PRIVATE.invalid", DisplayName = "PRIVATE-NAME", Enabled = id != "disabled", EmailConfirmed = id != "unconfirmed", SecurityStamp = id + "-stamp", IsAdmin = id == "admin" });
        foreach (var id in new[] { "event", "other", "empty" }) {
            var e = new SessionEvent { Id = id, Title = "Event <img src=x>", ScheduledDate = "2099-01-01T00:00:00.000Z", CreatedAt = "2026-10-01T00:00:00.000Z", ManualPhaseOverride = true };
            e.Phases.Add(new() { Id = id + "-hidden", EventId = id, Kind = "Hidden", Name = "Hidden", IsActive = id == "other" });
            e.Phases.Add(new() { Id = id + "-view", EventId = id, Kind = "View", Name = "View", IsActive = id != "other" });
            e.Phases.Add(new() { Id = id + "-custom", EventId = id, Kind = "Custom", Name = "Custom", MaxPicks = -1 });
            if (id == "event") e.Admins.Add(new() { EventId = id, UserId = "manager", GrantedAt = e.CreatedAt });
            db.SessionEvents.Add(e);
        }
        db.EventPartDefinitions.Add(new() { Id = "vo", EventId = "event", Name = "Voice", NormalizedName = "VOICE", Category = "Sing" });
        db.EventPartDefinitions.Add(new() { Id = "gt", EventId = "event", Name = "Guitar", NormalizedName = "GUITAR", Category = "Play" });
        db.Songs.Add(new() { Id = "song", EventId = "event", Title = "Current song", PerformanceSeconds = 60 });
        db.Songs.Add(new() { Id = "deleted-song", EventId = "event", Title = "Deleted song", PerformanceSeconds = 60, DeletedAt = "2026-10-01T00:00:00.000Z" });
        db.SongParts.Add(new() { Id = "voice", EventId = "event", SongId = "song", PartDefinitionId = "vo", AssignedUserId = "performer", PhaseId = "event-custom" });
        db.SongParts.Add(new() { Id = "guitar", EventId = "event", SongId = "song", PartDefinitionId = "gt", AssignedUserId = "performer", PhaseId = "event-custom" });
        db.SongParts.Add(new() { Id = "deleted-slot", EventId = "event", SongId = "deleted-song", PartDefinitionId = "vo", AssignedUserId = "deletedonly", PhaseId = "event-custom" });
        foreach (var id in new[] { "performer", "spectator", "disabled", "unconfirmed" })
            db.EventSpectators.Add(new() { EventId = "event", UserId = id, RegisteredBy = "admin", RegisteredAt = "2026-10-01T00:00:00.000Z" });
        db.EventSpectators.Add(new() { EventId = "other", UserId = "otherperson", RegisteredBy = "admin", RegisteredAt = "2026-10-01T00:00:00.000Z" });
        db.EventQuestions.Add(new() { Id = "private-q", EventId = "event", Text = "PRIVATE-QUESTION", Type = "Text" });
        db.EventQuestionAnswers.Add(new() { QuestionId = "private-q", UserId = "answeronly", Value = "PRIVATE-ANSWER" });
        await db.SaveChangesAsync();
    }

    ManualNotificationSubmit Edit(ManualNotificationPreview preview, string? request = null) =>
        new(request ?? Guid.NewGuid().ToString("D"), preview.Fingerprint, preview.Subject, preview.Body, true);
    Task<ManualNotificationAcceptance> Submit(ManualNotificationSubmit edit, string actor = "manager", string id = "event") =>
        core.AcceptEventManualNotificationAsync(Who(actor), id, edit);
    async Task Counts(int operations, int recipients, int messages, int audits, string label) {
        await using var db = await factory.CreateDbContextAsync();
        Check(await db.Set<ManualNotificationOperation>().CountAsync() == operations &&
            await db.Set<ManualNotificationRecipient>().CountAsync() == recipients &&
            await db.Messages.CountAsync() == messages + 1 &&
            await db.Audits.CountAsync(x => x.Action == "ManualNotificationAccepted") == audits, label);
    }
    var preview = await Preview();
    var edit = Edit(preview);
    await Deny<ValidationException>(() => Submit(edit with { Confirmed = false }), "No explicit confirmation means no acceptance");
    await Deny<ValidationException>(() => Submit(edit with { RequestId = "not-an-id" }), "Malformed operation ID rejected");
    await Deny<ValidationException>(() => Submit(edit with { RequestId = edit.RequestId.Replace("-", "") }), "Noncanonical operation ID rejected");
    await Deny<ValidationException>(() => Submit(edit with { PreviewFingerprint = "bad" }), "Malformed comparison rejected");
    await Deny<ValidationException>(() => Submit(edit with { Subject = "x\nY" }), "Header controls rejected at acceptance too");
    await Deny<UnauthorizedAccessException>(() => Submit(edit, "viewer"), "Forged administrator role cannot accept");
    await Deny<UnauthorizedAccessException>(() => core.AcceptEventManualNotificationAsync(new ClaimsPrincipal(), "event", edit), "Anonymous acceptance denied");
    await Deny<CoreConflictException>(() => Submit(edit with { Body = "changed after preview" }), "Changed draft requires fresh preview");
    using (var cts = new CancellationTokenSource()) { cts.Cancel(); await Deny<OperationCanceledException>(() => core.AcceptEventManualNotificationAsync(Who("manager"), "event", edit, cts.Token), "Cancelled request never accepted"); }
    await Counts(0, 0, 0, 0, "Rejected requests have no partial writes");
    await using (var db = await factory.CreateDbContextAsync()) { db.EventSpectators.Remove(await db.EventSpectators.SingleAsync(x => x.EventId == "event" && x.UserId == "spectator")); await db.SaveChangesAsync(); }
    await Deny<CoreConflictException>(() => Submit(edit), "Participant removal invalidates unaccepted preview");
    await using (var db = await factory.CreateDbContextAsync()) { db.EventSpectators.Add(new() { EventId = "event", UserId = "spectator", RegisteredBy = "admin", RegisteredAt = "2026-10-01T00:00:00.000Z" }); var e = await db.SessionEvents.SingleAsync(x => x.Id == "event"); e.RowVersion++; await db.SaveChangesAsync(); }
    await Deny<CoreConflictException>(() => Submit(edit), "Event revision invalidates unaccepted preview");
    edit = Edit(await Preview());
    // Force a failure in a later statement; operation, messages and recipient rows must roll back.
    await using (var db = await factory.CreateDbContextAsync()) {
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER manual_acceptance_failure BEFORE INSERT ON Audits WHEN NEW.Action='ManualNotificationAccepted' BEGIN SELECT RAISE(ABORT, 'synthetic acceptance failure'); END;");
    }
    await Deny<DbUpdateException>(() => Submit(edit), "Injected audit failure aborts whole acceptance");
    await Counts(0, 0, 0, 0, "No operation inbox recipient or audit survives rollback");
    await using (var db = await factory.CreateDbContextAsync()) { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER manual_acceptance_failure;"); }
    var accepted = await Submit(edit);
    Check(!accepted.Replayed && accepted.RequestId == edit.RequestId && accepted.RecipientCount == 2 && accepted.AcceptedAt > 0, "One accepted operation snapshots two distinct eligible recipients");
    await Counts(1, 2, 2, 1, "Atomic operation inbox recipient-level outbox and audit persisted");
    var replay = await Submit(edit);
    Check(replay.Replayed && replay.AcceptedAt == accepted.AcceptedAt && replay.RecipientCount == accepted.RecipientCount, "Same request safely recovers exact accepted outcome");
    await Counts(1, 2, 2, 1, "Replay creates no duplicates");
    await Deny<ValidationException>(() => Submit(edit with { Subject = "another subject" }), "Accepted operation ID cannot bind different content");
    await Deny<ValidationException>(() => Submit(edit with { PreviewFingerprint = new string('a',64) }), "Accepted operation ID cannot bind different comparison");
    await Deny<ValidationException>(() => Submit(edit, "admin"), "Accepted operation ID cannot bind another actor");
    await Deny<ValidationException>(() => Submit(edit, "admin", "other"), "Accepted operation ID cannot bind another event");
    await using (var db = await factory.CreateDbContextAsync()) {
        var op = await db.Set<ManualNotificationOperation>().SingleAsync();
        var recipients = await db.Set<ManualNotificationRecipient>().OrderBy(x => x.UserId).ToArrayAsync();
        var inbox = await db.Messages.Where(x=>x.Id!="legacy-message").OrderBy(x => x.UserId).ToArrayAsync();
        Check(op.Subject == edit.Subject && op.Body == edit.Body && op.PreviewFingerprint == edit.PreviewFingerprint, "Accepted content and preview provenance retained");
        Check(recipients.Select(x=>x.UserId).SequenceEqual(new[]{"performer","spectator"}) && recipients.All(x=>x.EventId=="event"&&x.OperationId==edit.RequestId&&x.PushState=="Pending"), "Immutable recipient-level Push intent is event-bound and dormant");
        Check(inbox.All(x=>x.Title==edit.Subject&&x.Body==edit.Body&&x.Created==accepted.AcceptedAt&&x.ReadAt==null) && inbox.Select(x=>x.Id).SequenceEqual(recipients.Select(x=>x.Id)), "Inbox content linked exactly once to snapshot and starts unread");
        Check(await db.Deliveries.CountAsync()==1 && await db.Set<EventPushDelivery>().CountAsync()==0 && await db.NotificationReservations.CountAsync()==0, "No legacy sends automatic Push or reservation overload");
        Check(await db.RegistrationOperations.CountAsync()==0 && await db.ReceptionOperations.CountAsync()==0 && await db.CommentOperations.CountAsync()==0, "Notification acceptance leaves business operations untouched");
    }
    await using (var db = await factory.CreateDbContextAsync()) { db.EventSpectators.Remove(await db.EventSpectators.SingleAsync(x=>x.EventId=="event"&&x.UserId=="spectator")); await db.SaveChangesAsync(); }
    Check((await Submit(edit)).Replayed && (await Preview()).RecipientCount==1, "Later membership change does not mutate historical accepted snapshot or resend");
    await Counts(1, 2, 2, 1, "Historical snapshot and inbox preserved after participant departure");
    await using (var db = await factory.CreateDbContextAsync()) { await SqliteStorageInitializer.InitializeAsync(db); Check(!db.Database.HasPendingModelChanges(), "Reinitialization and migration snapshot match after data writes"); }
    Check((await Submit(edit)).Replayed, "Fresh context after storage reinitialization recovers durable operation");
    using (var child = new Process()) {
        child.StartInfo = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput=true, RedirectStandardError=true };
        child.StartInfo.ArgumentList.Add("--resume"); child.StartInfo.ArgumentList.Add(root); child.StartInfo.ArgumentList.Add(edit.RequestId);
        child.Start();
        var output=child.StandardOutput.ReadToEndAsync(); var error=child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        Check(child.ExitCode==0 && (await output).Contains("Separate process replay recovery: PASS"), "Actual separate-process storage reopen recovers accepted operation without duplicates");
        if (child.ExitCode!=0) throw new Exception(await error);
    }
    var concurrentEdit = Edit(await Preview());
    var attempts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_=>Task.Run(()=>Submit(concurrentEdit))));
    Check(attempts.Count(x=>!x.Replayed)==1 && attempts.Count(x=>x.Replayed)==2, "Concurrent same-ID requests produce one acceptance and two replays");
    await Counts(2, 3, 3, 2, "Concurrent requests do not duplicate messages recipients or audits");
    await using (var db = await factory.CreateDbContextAsync()) { db.EventAdmins.Remove(await db.EventAdmins.SingleAsync()); await db.SaveChangesAsync(); }
    await Deny<UnauthorizedAccessException>(()=>Submit(edit), "Removed manager cannot read replay outcome");
    var adminEdit=Edit(await Preview("admin"));
    await using (var db = await factory.CreateDbContextAsync()) { (await db.Users.SingleAsync(x=>x.Id=="admin")).Enabled=false; await db.SaveChangesAsync(); }
    await Deny<UnauthorizedAccessException>(()=>Submit(adminEdit,"admin"), "Disabled administrator cannot accept");
    await using (var db = await factory.CreateDbContextAsync()) { var u=await db.Users.SingleAsync(x=>x.Id=="admin");u.Enabled=true;u.SecurityStamp="changed";await db.SaveChangesAsync(); }
    await Deny<UnauthorizedAccessException>(()=>Submit(adminEdit,"admin"), "Stale administrator security stamp cannot accept");
    await using (var db = await factory.CreateDbContextAsync()) { (await db.Users.SingleAsync(x=>x.Id=="admin")).SecurityStamp="admin-stamp";db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("admin-session"),UserId="admin",RevokedAt="2026-10-01T00:00:00.000Z"});await db.SaveChangesAsync(); }
    await Deny<UnauthorizedAccessException>(()=>Submit(adminEdit,"admin"), "Revoked administrator session cannot accept");
    await Counts(2,3,3,2,"Revocation failures create no side effects");
    await using (var db = await factory.CreateDbContextAsync()) {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText="PRAGMA integrity_check";
        Check((string?)await command.ExecuteScalarAsync()=="ok", "Final SQLite integrity_check is ok");
        command.CommandText="PRAGMA foreign_key_check";
        await using var reader=await command.ExecuteReaderAsync();
        Check(!await reader.ReadAsync(), "Final SQLite foreign_key_check has zero violations");
    }
    Console.WriteLine("Manual notification acceptance checks passed: "+passed);
    Console.WriteLine("::notice title=Manual notification acceptance checks::Passed "+passed+"; atomic inbox and dormant recipient outbox only; no transport or UI acceptance claimed.");
} finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
