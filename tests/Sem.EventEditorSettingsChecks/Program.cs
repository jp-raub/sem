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
var root = Path.Combine(Path.GetTempPath(), "sem-editor-settings-" + Guid.NewGuid()); Directory.CreateDirectory(root);
var path = Path.Combine(root, "core.db"); var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + path), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton(TimeProvider.System); Microsoft.Extensions.DependencyInjection.DataProtectionServiceCollectionExtensions.AddDataProtection(services);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>(); var passed = 0;
ClaimsPrincipal Principal(string id, string session = "session-1", string? stamp = null, bool forgedAdmin = false) => new(new ClaimsIdentity(new[]
{ new Claim(ClaimTypes.NameIdentifier, id), new Claim("AspNet.Identity.SecurityStamp", stamp ?? id + "-stamp"),
  new Claim(PasskeyAdditionService.SessionClaim, id + "-" + session), new Claim(ClaimTypes.Role, forgedAdmin ? "Admin" : "Member") }, "Identity.Application"));
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
async Task Deny<T>(Func<Task> operation, string name) where T : Exception
{ bool denied = false; try { await operation(); } catch (T) { denied = true; } Check(denied, name); }

async Task<EventView> Current(string id) => (await core.ReadAsync(Principal("admin"))).Events.Single(x => x.Id == id);
EventEdit Edit(EventView e, EventSettingsEdit? settings = null) => new(e.Title, e.Description, e.Location,
    DateTimeOffset.Parse(e.ScheduledDate), e.OpensAt is null ? null : DateTimeOffset.Parse(e.OpensAt),
    e.AnnouncementMessage, e.PhaseKind, e.ManagerIds, e.RowVersion, Settings: settings);
async Task<string> Snapshot()
{
    await using var db = await factory.CreateDbContextAsync();
    return JsonSerializer.Serialize(new {
        Events = await db.SessionEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Phases = await db.Phases.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Themes = await db.EventThemePreferences.AsNoTracking().OrderBy(x => x.EventId).ToArrayAsync(),
        Audits = await db.Audits.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
    });
}
try
{
    await using (var db = await factory.CreateDbContextAsync()) {
        await SqliteStorageInitializer.InitializeAsync(db);
        foreach (var uid in new[] { "admin", "manager", "member", "disabled", "unconfirmed" })
            db.Users.Add(new ApplicationUser { Id=uid, DisplayName=uid, UserName=uid+"@test.invalid", Email=uid+"@test.invalid",
                SecurityStamp=uid+"-stamp", IsAdmin=uid=="admin", Enabled=uid!="disabled", EmailConfirmed=uid!="unconfirmed" });
        await db.SaveChangesAsync();
    }
    var admin = Principal("admin"); var manager = Principal("manager");
    var catalog = await core.ReadThemeCatalogAsync(admin); var theme = catalog.First().Id;
    var create = new EventEdit("Settings fixture", "retained", "venue", DateTimeOffset.Parse("2099-01-01T00:00:00Z"), null, "notice", "View", ["manager"], 0);
    var legacyId = await core.SaveEventAsync(admin, null, create);
    var legacy = await Current(legacyId);
    Check(legacy.Capacity is null && legacy.Appearance == new ThemePreferenceView(null,null,0), "Legacy creation keeps unlimited capacity and inherited appearance");
    var id = await core.SaveEventAsync(admin, null, create with { Settings = new(4,new(theme,ThemeMode.Dark,0)) });
    var current = await Current(id);
    Check(current.Capacity==4 && current.Appearance==new ThemePreferenceView(theme,ThemeMode.Dark,1), "Creation saves overview, capacity and appearance together");
    Check((await core.ReadParticipationAsync(Principal("member"),id)).Capacity==4, "Participation reads the same capacity, not a duplicate setting");
    Check((await core.ReadThemeAppearanceAsync(Principal("member"),id)).Event==current.Appearance, "Display settings read the same appearance row");
    var appearance = current.Appearance!;
    await core.SaveEventAsync(manager,id,Edit(current,new(5,new(theme,ThemeMode.Light,appearance.RowVersion))) with { Title="Manager saved" });
    current = await Current(id);
    Check(current.Title=="Manager saved" && current.Capacity==5 && current.Appearance==new ThemePreferenceView(theme,ThemeMode.Light,2), "Assigned manager atomically updates all overview settings");
    var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var oldJson=JsonSerializer.Serialize(Edit(current),options);
    var json=System.Text.Json.Nodes.JsonNode.Parse(oldJson)!; json.AsObject().Remove("settings");
    await core.SaveEventAsync(admin,id,JsonSerializer.Deserialize<EventEdit>(json.ToJsonString(),options)!);
    var preserved=await Current(id);
    Check(preserved.Capacity==5 && preserved.Appearance==current.Appearance, "Omitted settings in old JSON preserve saved capacity and independent theme version");
    await core.SaveEventAsync(admin,id,Edit(preserved));
    current=await Current(id);
    Check(current.Capacity==5 && current.Appearance==preserved.Appearance, "Explicit null aggregate also preserves legacy settings");
    foreach (var bad in new[]{new EventSettingsEdit(-1,new(theme,ThemeMode.Light,2)),new(5,new("missing",ThemeMode.Light,2)),new(5,new(theme,(ThemeMode)999,2)),new(5,null!)}) {
        var before=await Snapshot();
        await Deny<ValidationException>(()=>core.SaveEventAsync(admin,id,Edit(current,bad) with {Title="must not persist",PhaseKind="Hidden"}),"Invalid capacity/theme/mode/null rejected");
        Check(before==await Snapshot(),"Invalid settings leave overview, phases, preferences and audit untouched");
    }
    foreach(var body in new[]{"{}","{\"capacity\":null}","{\"appearance\":null}","{\"capacity\":1,\"appearance\":null,\"unknown\":1}"}) {
        var denied=false;try{JsonSerializer.Deserialize<EventSettingsEdit>(body,options);}catch(JsonException){denied=true;}
        Check(denied,"Incomplete or unknown aggregate JSON cannot silently clear existing settings");
    }
    await using(var db=await factory.CreateDbContextAsync()) {
        db.EventSpectators.Add(new(){EventId=id,UserId="member",RegisteredBy="member",RegisteredAt="2098-01-01T00:00:00Z"});await db.SaveChangesAsync();
    }
    var beforeCapacity=await Snapshot();
    await Deny<ValidationException>(()=>core.SaveEventAsync(admin,id,Edit(current,new(0,new(theme,ThemeMode.Light,2)))) ,"Capacity below current unique spectators rejected");
    Check(beforeCapacity==await Snapshot(),"Capacity floor rejection is atomic");
    await core.SaveEventAsync(admin,id,Edit(current,new(1,new(theme,ThemeMode.Light,2))));
    current=await Current(id);
    Check(current.Capacity==1,"Capacity equal to existing unique participant count is allowed");
    await core.SaveEventThemePreferenceAsync(admin,id,new(theme,ThemeMode.Dark,current.Appearance!.RowVersion));
    var beforeThemeConflict=await Snapshot();
    await Deny<CoreConflictException>(()=>core.SaveEventAsync(admin,id,Edit(current,new(9,new(theme,ThemeMode.Light,current.Appearance!.RowVersion))) with{Title="stale theme"}),"Independent appearance editor invalidates stale overview theme version");
    Check(beforeThemeConflict==await Snapshot(),"Theme conflict preserves title, capacity, appearance and audit");
    current=await Current(id);
    await core.SetCapacityAsync(manager,id,new(current.RowVersion,2));
    var beforeEventConflict=await Snapshot();
    await Deny<CoreConflictException>(()=>core.SaveEventAsync(admin,id,Edit(current,new(9,new(null,null,current.Appearance!.RowVersion))) with{Title="stale event"}),"Participation management invalidates stale overview event version");
    Check(beforeEventConflict==await Snapshot(),"Event conflict does not partially update appearance");
    current=await Current(id);
    foreach(var actor in new[]{Principal("member"),Principal("disabled"),Principal("unconfirmed"),Principal("admin",stamp:"stale"),new ClaimsPrincipal(new ClaimsIdentity())}) {
        var before=await Snapshot();
        await Deny<UnauthorizedAccessException>(()=>core.SaveEventAsync(actor,id,Edit(current,new(9,new(null,null,current.Appearance!.RowVersion)))),"Unauthorized actor cannot alter integrated settings");
        Check(before==await Snapshot(),"Authorization rejection writes nothing");
    }
    await core.SaveEventAsync(manager,id,Edit(current,new(null,new(null,null,current.Appearance!.RowVersion))));
    current=await Current(id);
    Check(current.Capacity is null && current.Appearance!.ThemeId is null && current.Appearance.Mode is null,"Explicit blank fields clear capacity and restore appearance inheritance");
    var emptyId=await core.SaveEventAsync(admin,null,create with{Settings=new(0,new(null,ThemeMode.System,0))});
    Check((await Current(emptyId)).Capacity==0 && (await Current(emptyId)).Appearance!.Mode==ThemeMode.System,"Empty event allows zero capacity and explicit system mode distinct from inheritance");
    await using(var db=await factory.CreateDbContextAsync()){
        db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("admin-revoked"),UserId="admin",RevokedAt="2026-10-19T00:00:00.000Z"});await db.SaveChangesAsync();
    }
    await Deny<UnauthorizedAccessException>(()=>core.SaveEventAsync(Principal("admin","revoked"),id,Edit(current,new(2,new(null,null,current.Appearance!.RowVersion)))),"Revoked session cannot save settings");
    Console.WriteLine($"PASS total={passed}");
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
