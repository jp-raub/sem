using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-manual-preview-" + Guid.NewGuid());
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
Task Invalid(string? subject, string? body, string label) => Deny<ValidationException>(
    () => { ManualNotificationDraftPolicy.Normalize(subject, body); return Task.CompletedTask; }, label);
ClaimsPrincipal Who(string id) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, id), new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim, id + "-session"), new Claim(ClaimTypes.Role, "Admin")
], "Identity.Application"));
Task<ManualNotificationPreview> Preview(string actor = "manager", string id = "event", string subject = " 件名 ", string body = " 本文 ") =>
    core.PreviewEventManualNotificationAsync(Who(actor), id, subject, body);
async Task Phase(string kind) {
    await using var db = await factory.CreateDbContextAsync();
    var phases = await db.Phases.Where(x => x.EventId == "event").ToArrayAsync();
    phases.Single(x => x.IsActive).IsActive = false; await db.SaveChangesAsync();
    phases.Single(x => x.Kind == kind).IsActive = true; await db.SaveChangesAsync();
}
try {
    await Invalid(null, "body", "Null subject rejected");
    await Invalid(" ", "body", "Blank subject rejected");
    await Invalid(new string('a', 81), "body", "Subject over 80 UTF-16 units rejected");
    await Invalid("subject\r\nInjected: header", "body", "Subject header controls rejected");
    await Invalid("subject\t", "body", "Subject tab rejected before trim");
    await Invalid("subject", null, "Null body rejected");
    await Invalid("subject", " \n ", "Blank body rejected");
    await Invalid("subject", new string('b', 2001), "body over 2000 UTF-16 units rejected");
    await Invalid("subject", "bad\0body", "Body NUL rejected");
    await Invalid("subject", "bad\u001bbody", "Body escape control rejected");
    Check(ManualNotificationDraftPolicy.Normalize(new string('a', 80), new string('b', 2000)).Body.Length == 2000, "Exact maximum lengths accepted");
    Check(ManualNotificationDraftPolicy.Normalize(" 題名 ", " line1\r\n\tline2 ").Body == "line1\r\n\tline2", "Multiline and tabs preserved with outer trim only");
    Check(ManualNotificationDraftPolicy.Normalize("<img src=x>", "<script>literal</script>").Subject == "<img src=x>", "Markup remains literal draft text");

    await using (var db = await factory.CreateDbContextAsync()) {
        await SqliteStorageInitializer.InitializeAsync(db);
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
    await Deny<UnauthorizedAccessException>(() => core.PreviewEventManualNotificationAsync(new ClaimsPrincipal(), "event", "s", "b"), "Anonymous denied");
    await Deny<UnauthorizedAccessException>(() => Preview("viewer"), "Forged administrator role cannot create preview");
    await Deny<UnauthorizedAccessException>(() => Preview("manager", "other"), "Manager assignment cannot cross event boundary");
    await Deny<UnauthorizedAccessException>(() => Preview("admin", "missing"), "Unknown event denied");
    await Deny<ValidationException>(() => Preview("admin", "empty"), "Zero current recipients rejected");
    var first = await Preview();
    Check(first.RecipientCount == 2, "Distinct live performers plus spectators; disabled unconfirmed other-event answer-only and deleted-song users excluded");
    Check(first.Subject == "件名" && first.Body == "本文", "Normalized draft returned");
    Check(first.EventTitle == "Event <img src=x>" && first.EventId == "event", "Public event identity retained as literal text");
    Check(first.PushPreviewBody == "新しいメッセージがあります", "Push preview never contains full message body");
    var json = JsonSerializer.Serialize(first);
    Check(!json.Contains("PRIVATE") && !json.Contains("SecurityStamp") && !json.Contains("performer") && !json.Contains("spectator"), "DTO exposes neither recipient IDs nor private contact profile or answer values");
    Check(first.Fingerprint.Length == 64 && (await Preview()).Fingerprint == first.Fingerprint, "Unchanged snapshot has stable SHA-256 comparison");
    Check((await Preview(subject: "別件名")).Fingerprint != first.Fingerprint, "Changed subject changes comparison");
    Check((await Preview(body: "別本文")).Fingerprint != first.Fingerprint, "Changed body changes comparison");
    Check((await Preview("admin")).Fingerprint != first.Fingerprint, "Preview comparison bound to current actor");
    await using (var db = await factory.CreateDbContextAsync()) { var e = await db.SessionEvents.SingleAsync(x => x.Id == "event"); e.RowVersion++; await db.SaveChangesAsync(); }
    Check((await Preview()).Fingerprint != first.Fingerprint, "Changed event revision invalidates comparison");
    await using (var db = await factory.CreateDbContextAsync()) { db.EventSpectators.Remove(await db.EventSpectators.SingleAsync(x => x.EventId == "event" && x.UserId == "spectator")); await db.SaveChangesAsync(); }
    var one = await Preview(); Check(one.RecipientCount == 1 && one.Fingerprint != first.Fingerprint, "Removed current participant changes count and comparison");
    await using (var db = await factory.CreateDbContextAsync()) { (await db.EventPartDefinitions.SingleAsync(x => x.Id == "vo")).DeletedAt = "2026-10-01T00:00:00.000Z"; await db.SaveChangesAsync(); }
    Check((await Preview()).RecipientCount == 1, "Live second part and spectator deduplicate after first definition deleted");
    await Phase("Hidden");
    await Deny<ValidationException>(() => Preview(), "Hidden event excludes ordinary participant recipients even for manager preview");
    await using (var db = await factory.CreateDbContextAsync()) { db.EventSpectators.Add(new() { EventId = "event", UserId = "manager", RegisteredBy = "admin", RegisteredAt = "2026-10-01T00:00:00.000Z" }); await db.SaveChangesAsync(); }
    Check((await Preview()).RecipientCount == 1, "Hidden event permits a currently assigned participating manager only");
    await Phase("View");
    await using (var db = await factory.CreateDbContextAsync()) { db.EventAdmins.Remove(await db.EventAdmins.SingleAsync()); await db.SaveChangesAsync(); }
    await Deny<UnauthorizedAccessException>(() => Preview(), "Removed manager denied on next read despite forged role");
    await using (var db = await factory.CreateDbContextAsync()) { var u = await db.Users.SingleAsync(x => x.Id == "admin"); u.SecurityStamp = "changed"; await db.SaveChangesAsync(); }
    await Deny<UnauthorizedAccessException>(() => Preview("admin"), "Stale administrator security stamp denied");
    await using (var db = await factory.CreateDbContextAsync()) { (await db.Users.SingleAsync(x => x.Id == "admin")).SecurityStamp = "admin-stamp"; db.RevokedAuthSessions.Add(new() { TokenHash = SemEndpoints.Hash("admin-session"), UserId = "admin", RevokedAt = "2026-10-01T00:00:00.000Z" }); await db.SaveChangesAsync(); }
    await Deny<UnauthorizedAccessException>(() => Preview("admin"), "Revoked administrator session denied");
    await using (var db = await factory.CreateDbContextAsync()) {
        Check(!db.Database.HasPendingModelChanges(), "No model or migration change");
        Check(await db.Messages.CountAsync() == 0 && await db.Deliveries.CountAsync() == 0 && await db.Set<EventPushDelivery>().CountAsync() == 0 && await db.Audits.CountAsync() == 0, "Preview does not create inbox mail Push or audit rows");
        Check(await db.RegistrationOperations.CountAsync() == 0 && await db.NotificationReservations.CountAsync() == 0 && await db.ReceptionOperations.CountAsync() == 0, "Preview does not mutate registration reservation or reception operations");
    }
    Console.WriteLine("Manual notification preview checks passed: " + passed);
    Console.WriteLine("::notice title=Manual notification preview checks::Passed " + passed + "; read-only backend only; no UI or transport acceptance claimed.");
} finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
