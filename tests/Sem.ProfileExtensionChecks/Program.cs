using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Microsoft.Data.Sqlite;
using Sem.Web.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sem.Web.Core;
using Sem.Web.Data;

var root = Path.Combine(Path.GetTempPath(), "sem-profile-extension-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var clock = new TestClock { Now = DateTimeOffset.Parse("2026-10-15T09:00:00Z") };
var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite("Data Source=" + Path.Combine(root, "test.db")), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddDataProtection();
services.AddSingleton<TimeProvider>(clock);
services.AddSingleton<BusinessNotificationStore>();
services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
var passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
async Task Deny<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); } catch (T) { Check(true, label); return; }
    throw new Exception("FAIL " + label);
}
ClaimsPrincipal Who(string id) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, id), new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim, id + "-session")], "Identity.Application"));
try
{
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.Database.MigrateAsync();
        db.Users.AddRange(
            new ApplicationUser { Id = "admin", UserName = "admin@test.invalid", Email = "admin@test.invalid", DisplayName = "Admin", IsAdmin = true, Enabled = true, EmailConfirmed = true, SecurityStamp = "admin-stamp" },
            new ApplicationUser { Id = "member", UserName = "member@test.invalid", Email = "private@test.invalid", DisplayName = "Member", Enabled = true, EmailConfirmed = true, SecurityStamp = "member-stamp" },
            new ApplicationUser { Id = "other", UserName = "other@test.invalid", Email = "other@test.invalid", DisplayName = "Other", Enabled = true, EmailConfirmed = true, SecurityStamp = "other-stamp" });
        await db.SaveChangesAsync();
    }

    var addedText = await core.SaveProfileQuestionAsync(Who("admin"), new("Add", null, "好きな曲は？", "Text", 0));
    var qText = addedText.Questions.Single(q => q.Text == "好きな曲は？");
    var addedYesNo = await core.SaveProfileQuestionAsync(Who("admin"), new("Add", null, "参加希望ですか？", "YesNo", 0));
    var qYesNo = addedYesNo.Questions.Single(q => q.Text == "参加希望ですか？");
    var addedUnused = await core.SaveProfileQuestionAsync(Who("admin"), new("Add", null, "未回答の設問", "Text", 0));
    var qUnused = addedUnused.Questions.Single(q => q.Text == "未回答の設問");
    await Deny<UnauthorizedAccessException>(() => core.SaveProfileQuestionAsync(Who("member"), new("Add", null, "私設", "Text", 0)), "global-admin-only question writes");

    await Deny<ValidationException>(() => core.SaveProfileEditorAsync(Who("member"), new("Member", "", "", [], "", 0, [new(null!, "value")])), "null question ID rejects without writes");
    await Deny<ValidationException>(() => core.SaveProfileEditorAsync(Who("member"), new("Member", "", "", [], "", 0, [new("", "value")])), "empty question ID rejects without writes");
    await Deny<ValidationException>(() => core.SaveProfileQuestionAsync(Who("admin"), new("Reorder", null, null, null, 0, [null!])), "null reorder item rejects without writes");
    await Deny<ValidationException>(() => core.SaveProfileQuestionAsync(Who("admin"), new("Reorder", null, null, null, 0, [new("", 0)])), "empty reorder ID rejects without writes");
    await Deny<ValidationException>(() => core.SaveProfileEditorAsync(Who("member"), new("Member", "", "", Enumerable.Range(0,100).Select(_ => new SocialLink("Website", "https://example.test/" + new string('x',400))).ToArray(), "", 0, [])), "new editor preserves prior32000-character serialized SNS cap");
    var editor = await core.ReadProfileEditorAsync(Who("member"));
    Check(editor.Profile.Answers!.Length == 3 && editor.Profile.Answers.All(x => x.Value is null), "unanswered questions are optional and visible to self");
    await core.SaveProfileEditorAsync(Who("member"), new("Member", "めんばー", "めんばー", [new("Website", "https://example.test/")], "🎸", 0,
        [new(qText.Id, "  Session tune  "), new(qYesNo.Id, "YES")]));
    await Deny<SqliteException>(async () => { await using var constrainedDb=await factory.CreateDbContextAsync(); await constrainedDb.Database.ExecuteSqlRawAsync("UPDATE UserProfiles SET Icon = {0} WHERE UserId = 'member'", new string('x',65)); }, "migrated database enforces Icon length check without changing stored profile");
    var visible = await core.ReadProfileAsync(Who("admin"), "member");
    Check(visible.Icon == "🎸" && visible.Answers!.Single(x => x.QuestionId == qText.Id).Value == "Session tune", "saved emoji and trimmed text answer appear in member profile");
    Check(visible.Answers!.Single(x => x.QuestionId == qYesNo.Id).Value == "YES", "YesNo answer uses constrained values");
    await Deny<ValidationException>(() => core.SaveProfileEditorAsync(Who("member"), new("Member", "", "", [], "plain text", 1, [])), "plain-text profile icon rejected");
    await Deny<ValidationException>(() => core.SaveProfileEditorAsync(Who("member"), new("Member", "", "", [], "", 1, [new(qText.Id, new string('x', 1001))])), "answer text over 1000 rejected");

    await Deny<ValidationException>(() => core.SaveProfileQuestionAsync(Who("admin"), new("Edit", qText.Id, "Changed", "YesNo", qText.RowVersion)), "question type immutable");
    var edited = await core.SaveProfileQuestionAsync(Who("admin"), new("Edit", qText.Id, "どんな音楽が好き？", "Text", qText.RowVersion));
    var editedText = edited.Questions.Single(q => q.Id == qText.Id);
    Check(editedText.HasAnswers && editedText.Text == "どんな音楽が好き？", "answered question text edit is allowed and answer is retained");
    await Deny<ValidationException>(() => core.SaveProfileQuestionAsync(Who("admin"), new("Delete", qText.Id, null, null, editedText.RowVersion)), "answered question deletion is rejected");
    var stopped = await core.SaveProfileQuestionAsync(Who("admin"), new("Stop", qText.Id, null, null, editedText.RowVersion));
    Check(!stopped.Questions.Single(q => q.Id == qText.Id).Enabled, "answered question can be stopped");
    var memberAfterStop = await core.ReadProfileAsync(Who("admin"), "member");
    Check(!memberAfterStop.Answers!.Any(x => x.QuestionId == qText.Id), "stopped question is hidden from member and guest projection");
    await using (var retainedDb = await factory.CreateDbContextAsync())
        Check(await retainedDb.ProfileAnswers.AnyAsync(a => a.UserId == "member" && a.QuestionId == qText.Id && a.Value == "Session tune"), "stopped answer remains retained in storage");
    await Deny<ValidationException>(() => core.SaveProfileEditorAsync(Who("member"), new("Member", "", "", [], "", 1, [new(qText.Id, "overwrite")])) , "stopped question cannot accept a new answer");

    var reordered = await core.SaveProfileQuestionAsync(Who("admin"), new("Reorder", null, null, null, 0,
        stopped.Questions.OrderByDescending(q => q.SortOrder).Select(q => new ProfileQuestionOrderItem(q.Id, q.RowVersion)).ToArray()));
    Check(reordered.Questions.OrderBy(q => q.SortOrder).First().Id == qUnused.Id, "question reorder updates versioned rows");
    var deleted = reordered.Questions.Single(q => q.Id == qUnused.Id);
    await core.SaveProfileQuestionAsync(Who("admin"), new("Delete", deleted.Id, null, null, deleted.RowVersion));

    var issued = await core.IssueProfileQrAsync(Who("member"), new(0, false));
    Check(issued.ExpiresAt == clock.Now.AddHours(24).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), "profile QR expiry is exactly 24 hours");
    await using (var db = await factory.CreateDbContextAsync())
    {
        var stored = await db.ProfileQrCredentials.SingleAsync(x => x.UserId == "member");
        Check(stored.TokenHash.Length == 64 && stored.TokenHash != issued.Token && !stored.TokenHash.Contains(issued.Token), "database stores only the token hash");
    }
    var svg = await core.ReadProfileQrSvgAsync(Who("member"), issued.Token, issued.RowVersion, "https://profile.example/");
    Check(svg.Contains("<svg", StringComparison.Ordinal) && !svg.Contains(issued.Token, StringComparison.Ordinal) && ProfileQrImage.Link("https://profile.example/", issued.Token).Contains("profile/share#token=" + issued.Token), "owner-only SVG encodes a fragment-based bearer without exposing its text in SVG source");
    await Deny<UnauthorizedAccessException>(() => core.ReadProfileQrSvgAsync(Who("other"), issued.Token, issued.RowVersion, "https://profile.example/"), "another member cannot fetch owner's QR SVG");

    var publicProfile = await core.ResolveProfileQrAsync(issued.Token);
    Check(publicProfile.DisplayName == "Member" && publicProfile.Answers!.Single(x => x.QuestionId == qYesNo.Id).Value == "YES" && !publicProfile.Answers!.Any(x => x.QuestionId == qText.Id), "anonymous bearer view contains active answers only and hides stopped answers");
    var json = System.Text.Json.JsonSerializer.Serialize(publicProfile);
    Check(!json.Contains("private@test.invalid", StringComparison.Ordinal) && !json.Contains("Events", StringComparison.OrdinalIgnoreCase), "public view omits email and registration history");
    var statusBeforeRead = await core.ReadProfileQrStatusAsync(Who("member"));
    clock.Now = clock.Now.AddHours(23);
    var stillValid = await core.ResolveProfileQrAsync(issued.Token);
    Check(stillValid.UserId == "member", "repeated read remains valid without extending fixed expiry");
    var statusAfterRead = await core.ReadProfileQrStatusAsync(Who("member"));
    Check(statusAfterRead.ExpiresAt == statusBeforeRead.ExpiresAt, "QR read does not extend expiry");
    var reissued = await core.IssueProfileQrAsync(Who("member"), new(statusAfterRead.RowVersion, true));
    await Deny<UnauthorizedAccessException>(() => core.ResolveProfileQrAsync(issued.Token), "reissue immediately invalidates previous bearer");
    Check((DateTimeOffset.Parse(reissued.ExpiresAt) - clock.Now).TotalHours == 24, "reissue starts a fresh exact 24-hour period");
    await using (var auditDb=await factory.CreateDbContextAsync()) {
        var rows=await auditDb.Audits.Where(a=>a.Action.StartsWith("ProfileQRLink")).ToArrayAsync();
        Check(rows.Length==2 && rows.All(a=>a.ActorId=="member" && a.EntityId=="member"), "QR issue and reissue audit actor and target atomically");
        var auditJson=System.Text.Json.JsonSerializer.Serialize(rows);
        Check(!auditJson.Contains(issued.Token) && !auditJson.Contains(reissued.Token), "QR audit never stores bearer tokens");
    }
    clock.Now = clock.Now.AddHours(24);
    await Deny<UnauthorizedAccessException>(() => core.ResolveProfileQrAsync(reissued.Token), "QR expires at its fixed expiry instant");

    await using (var db = await factory.CreateDbContextAsync())
    {
        var target = await db.Users.SingleAsync(u => u.Id == "member"); target.Enabled = false; await db.SaveChangesAsync();
    }
    await Deny<UnauthorizedAccessException>(() => core.ResolveProfileQrAsync(reissued.Token), "disabled target immediately loses QR view access");
    Console.WriteLine($"Profile extension checks passed: {passed}");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}

sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
