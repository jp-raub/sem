using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string Origin = "https://sem-theme-test.raub.jp";
var root = Path.Combine(Path.GetTempPath(), "sem-theme-http-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var database = Path.Combine(root, "test.db");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    EnvironmentName = "Development",
    ApplicationName = typeof(CoreEndpoints).Assembly.GetName().Name
});
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + database), ServiceLifetime.Scoped);
builder.Services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<BusinessNotificationStore>();
builder.Services.AddScoped<CoreFoundationService>();
builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = Origin + "/", DataDirectory = root });
builder.Services.AddSingleton(new ManualPushRuntime(false));
builder.Services.AddSingleton<IEventPushTransport, NoopPushTransport>();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "theme-test-csrf";
    o.Cookie.SecurePolicy = CookieSecurePolicy.None;
});
builder.Services.AddAuthentication("SyntheticCookie")
    .AddScheme<AuthenticationSchemeOptions, SyntheticCookieHandler>("SyntheticCookie", _ => { });
var app = builder.Build();
app.UseAuthentication();
app.UseAntiforgery();
app.MapGet("/__csrf", (HttpContext ctx, IAntiforgery anti) =>
    Results.Json(new { token = anti.GetAndStoreTokens(ctx).RequestToken }));
app.MapCoreFoundation();

var checks = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var started = false;
try
{
    await using (var init = app.Services.CreateAsyncScope())
    {
        var factory = init.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        foreach (var uid in new[] { "admin", "manager", "member" })
            db.Users.Add(new ApplicationUser
            {
                Id = uid,
                UserName = uid,
                Email = uid + "@PRIVATE.invalid",
                DisplayName = uid,
                Enabled = true,
                EmailConfirmed = true,
                SecurityStamp = uid + "-stamp",
                IsAdmin = uid == "admin"
            });
        await db.SaveChangesAsync();
    }

    string hiddenEventId;
    string visibleEventId;
    await using (var seed = app.Services.CreateAsyncScope())
    {
        var core = seed.ServiceProvider.GetRequiredService<CoreFoundationService>();
        hiddenEventId = await core.SaveEventAsync(Who("admin"), null,
            new("HTTP hidden event", "", "", DateTimeOffset.Parse("2099-01-01T00:00:00Z"), null, "", "Hidden", ["manager"], 0));
        visibleEventId = await core.SaveEventAsync(Who("admin"), null,
            new("HTTP visible event", "", "", DateTimeOffset.Parse("2099-01-01T00:00:00Z"), null, "", "View", ["manager"], 0));
    }

    await app.StartAsync();
    started = true;
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };

    async Task<(string Token, string Cookie)> CsrfFor(string identity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__csrf");
        request.Headers.TryAddWithoutValidation("Cookie", "test-auth=" + identity);
        using var response = await client.SendAsync(request);
        Check(response.IsSuccessStatusCode, "test host issues antiforgery token for " + identity);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("theme-test-csrf=", StringComparison.Ordinal)).Split(';', 2)[0];
        return (json.RootElement.GetProperty("token").GetString()!, cookie);
    }

    var adminCsrf = await CsrfFor("admin");
    var managerCsrf = await CsrfFor("manager");
    var memberCsrf = await CsrfFor("member");
    string Cookie(string identity, (string Token, string Cookie) csrf) => $"test-auth={identity}; {csrf.Cookie}";

    async Task<HttpResponseMessage> Get(string path, string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        var response = await client.SendAsync(request);
        Check(response.Headers.CacheControl?.NoStore == true, path + " is no-store");
        return response;
    }

    async Task<HttpResponseMessage> Post(
        string action, string query, string body, string identity, (string Token, string Cookie) csrf,
        bool includeToken = true, bool includeOrigin = true, string? cookieOverride = null, bool chunked = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/core/" + action + (query.Length == 0 ? "" : "?" + query));
        var bytes = Encoding.UTF8.GetBytes(body);
        request.Content = chunked ? new StreamContent(new NonSeekableReadStream(bytes)) : new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (includeOrigin) request.Headers.TryAddWithoutValidation("Origin", Origin);
        if (includeToken) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf.Token);
        request.Headers.TryAddWithoutValidation("Cookie", cookieOverride ?? Cookie(identity, csrf));
        var response = await client.SendAsync(request);
        Check(response.Headers.CacheControl?.NoStore == true, action + " POST is no-store");
        return response;
    }

    // Event header JSON is bounded independently; all older theme/profile limits remain unchanged.
    const string headerPng="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFAAAAAoCAIAAADmAupWAAAATklEQVR4nO3PAQ3AIADAMMAN7pCA9Lv4k71VsM19z/iT9XXA2wzXGa4zXGe4znCd4TrDdYbrDNcZrjNcZ7jOcJ3hOsN1husM1xmuM1z3AF2YAWlbNButAAAAAElFTkSuQmCC";
    var headerEdit=new EventEdit("HTTP header event", "", "", DateTimeOffset.Parse("2099-01-01T00:00:00Z"),null,"","View",[],0, HeaderImageDataUrl:headerPng);
    var headerBody=JsonSerializer.Serialize(headerEdit,new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using(var largeEnvelope=await Post("event","",new string(' ',70000)+headerBody,"admin",adminCsrf)) {
        if(!largeEnvelope.IsSuccessStatusCode) Console.WriteLine("Header HTTP diagnostic: "+largeEnvelope.StatusCode+" "+await largeEnvelope.Content.ReadAsStringAsync());
        Check(largeEnvelope.StatusCode==HttpStatusCode.OK,"Event-only image envelope supports more than old 64KiB without weakening other action limits"); }
    foreach(var chunked in new[]{false,true})
        using(var oversizedHeader=await Post("event","",new string(' ',2*1024*1024+1),"admin",adminCsrf,chunked:chunked))
            Check(oversizedHeader.StatusCode==HttpStatusCode.RequestEntityTooLarge,"Event image JSON rejects oversized "+(chunked?"streamed":"declared")+" body");
    using(var missingCsrf=await Post("event","",headerBody,"admin",adminCsrf,includeToken:false))
        Check(missingCsrf.StatusCode==HttpStatusCode.BadRequest,"Event image upload retains CSRF requirement");
    using(var wrongOrigin=await Post("event","",headerBody,"admin",adminCsrf,includeOrigin:false))
        Check(wrongOrigin.StatusCode==HttpStatusCode.Forbidden,"Event image upload retains Origin requirement");
    using(var ordinaryMember=await Post("event","",headerBody,"member",memberCsrf))
        Check(ordinaryMember.StatusCode==HttpStatusCode.Forbidden,"Ordinary member cannot create an image event via HTTP");
    using(var unknown=await Post("event","",headerBody[..^1]+",\"unexpected\":true}","admin",adminCsrf))
        Check(unknown.StatusCode==HttpStatusCode.BadRequest,"Event image write rejects unknown JSON properties");

    // Overview's optional aggregate shares the strict event transport and commits all settings in one transaction.
    var editorOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var settingsEdit = new EventEdit("HTTP aggregate", "", "", DateTimeOffset.Parse("2099-01-01T00:00:00Z"),null,"","View",["manager"],0,
        Settings:new(3,new("rose",ThemeMode.Dark,0)));
    string aggregateId;
    using(var created=await Post("event","",JsonSerializer.Serialize(settingsEdit,editorOptions),"admin",adminCsrf)) {
        Check(created.StatusCode==HttpStatusCode.OK,"HTTP overview aggregate creates event and settings");
        using var result=JsonDocument.Parse(await created.Content.ReadAsStringAsync());aggregateId=result.RootElement.GetProperty("id").GetString()!;
    }
    async Task<EventView> AggregateView() {
        await using var scope=app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<CoreFoundationService>().ReadAsync(Who("admin"))).Events.Single(e=>e.Id==aggregateId);
    }
    var aggregate=await AggregateView();
    Check(aggregate.Capacity==3&&aggregate.Appearance==new ThemePreferenceView("rose",ThemeMode.Dark,1),"HTTP read exposes saved capacity and independent appearance version");
    var validBody=JsonSerializer.Serialize(settingsEdit with{RowVersion=aggregate.RowVersion,Settings=new(4,new("rose",ThemeMode.Light,1))},editorOptions);
    foreach(var badSettings in new[]{"{}","{\"capacity\":null}","{\"capacity\":2,\"appearance\":null}","{\"capacity\":2,\"capacity\":3,\"appearance\":{\"themeId\":null,\"mode\":null,\"rowVersion\":1}}","{\"capacity\":2,\"appearance\":{\"themeId\":null,\"mode\":99,\"rowVersion\":1}}","{\"capacity\":2,\"appearance\":{\"themeId\":null,\"mode\":null,\"rowVersion\":1,\"extra\":true}}"}) {
        // Keep duplicate-property bytes intact, rather than normalizing them through JsonNode.
        var raw=validBody[..validBody.IndexOf("\"settings\":",StringComparison.Ordinal)]+"\"settings\":"+badSettings+"}";
        using var rejected=await Post("event","id="+aggregateId,raw,"manager",managerCsrf);
        Check(rejected.StatusCode==HttpStatusCode.BadRequest,"Invalid nested aggregate JSON returns 400");
        Check(JsonSerializer.Serialize(await AggregateView())==JsonSerializer.Serialize(aggregate),"Invalid HTTP aggregate never partially writes event/settings");
    }
    using(var unauthorized=await Post("event","id="+aggregateId,validBody,"member",memberCsrf))
        Check(unauthorized.StatusCode==HttpStatusCode.Forbidden,"Member cannot alter overview capacity or theme via aggregate");
    using(var updated=await Post("event","id="+aggregateId,validBody,"manager",managerCsrf))
        Check(updated.StatusCode==HttpStatusCode.OK,"Assigned manager can save aggregate over HTTP");
    using(var stale=await Post("event","id="+aggregateId,validBody,"manager",managerCsrf))
        Check(stale.StatusCode==HttpStatusCode.Conflict,"Stale aggregate returns HTTP 409 instead of partial save");
    aggregate=await AggregateView();
    var legacyNode=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(settingsEdit with{RowVersion=aggregate.RowVersion},editorOptions))!;
    legacyNode.AsObject().Remove("settings");
    using(var legacyWrite=await Post("event","id="+aggregateId,legacyNode.ToJsonString(),"manager",managerCsrf))
        Check(legacyWrite.StatusCode==HttpStatusCode.OK,"Old HTTP client without aggregate remains compatible");
    var afterLegacy=await AggregateView();
    Check(afterLegacy.Capacity==aggregate.Capacity&&afterLegacy.Appearance==aggregate.Appearance,"Legacy HTTP write preserves settings and appearance version");

    string Expected(string actor) => "expectedActor=" + Uri.EscapeDataString(actor);
    string Id(string value) => "id=" + Uri.EscapeDataString(value);

    using (var anonymous = await Get("/api/core/theme-appearance"))
        Check(anonymous.StatusCode == HttpStatusCode.Forbidden, "anonymous appearance read is denied");
    using (var memberView = await Get("/api/core/theme-appearance", Cookie("member", memberCsrf)))
        Check(memberView.StatusCode == HttpStatusCode.OK, "authenticated member can read own appearance");
    using (var hiddenManager = await Get("/api/core/theme-appearance?" + Id(hiddenEventId), Cookie("manager", managerCsrf)))
        Check(hiddenManager.StatusCode == HttpStatusCode.OK, "manager can read appearance for hidden event");
    using (var hiddenAdmin = await Get("/api/core/theme-appearance?" + Id(hiddenEventId), Cookie("admin", adminCsrf)))
        Check(hiddenAdmin.StatusCode == HttpStatusCode.OK, "global administrator can read appearance for hidden event");
    using (var hiddenMember = await Get("/api/core/theme-appearance?" + Id(hiddenEventId), Cookie("member", memberCsrf)))
        Check(hiddenMember.StatusCode == HttpStatusCode.Forbidden, "hidden-event appearance is not disclosed to non-manager");
    using (var visibleMember = await Get("/api/core/theme-appearance?" + Id(visibleEventId), Cookie("member", memberCsrf)))
        Check(visibleMember.StatusCode == HttpStatusCode.OK, "participant-accessible visible event can be read");
    using (var emptyId = await Get("/api/core/theme-appearance?id=", Cookie("member", memberCsrf)))
        Check(emptyId.StatusCode == HttpStatusCode.BadRequest, "event id cannot be empty");
    using (var duplicateId = await Get("/api/core/theme-appearance?" + Id(visibleEventId) + "&" + Id(visibleEventId), Cookie("member", memberCsrf)))
        Check(duplicateId.StatusCode == HttpStatusCode.BadRequest, "event id must occur at most once");
    using (var longId = await Get("/api/core/theme-appearance?" + Id(new string('x', 129)), Cookie("member", memberCsrf)))
        Check(longId.StatusCode == HttpStatusCode.BadRequest, "event id length is bounded");
    using (var controlId = await Get("/api/core/theme-appearance?" + Id("event\u0001id"), Cookie("member", memberCsrf)))
        Check(controlId.StatusCode == HttpStatusCode.BadRequest, "event id rejects control characters");

    using (var postRead = await Post("theme-appearance", Expected("member"), "{", "member", memberCsrf, includeToken: false))
        Check(postRead.StatusCode == HttpStatusCode.MethodNotAllowed, "appearance POST is 405 before body or CSRF handling");
    using (var getWrite = await Get("/api/core/theme-personal", Cookie("member", memberCsrf)))
        Check(getWrite.StatusCode == HttpStatusCode.MethodNotAllowed, "personal preference GET is 405");

    // The response exposes only the current actor's personal selection; `user` is not a selector.
    using (var managerPreference = await Post("theme-personal", Expected("manager"),
               """{"themeId":"rose","mode":2,"rowVersion":0}""", "manager", managerCsrf))
        Check(managerPreference.StatusCode == HttpStatusCode.OK, "manager can save own preference");
    using (var ownOnly = await Get("/api/core/theme-appearance?user=manager", Cookie("member", memberCsrf)))
    {
        Check(ownOnly.StatusCode == HttpStatusCode.OK, "unknown user selector does not block own appearance read");
        using var json = JsonDocument.Parse(await ownOnly.Content.ReadAsStringAsync());
        var appearance = json.RootElement.GetProperty("appearance");
        Check(json.RootElement.GetProperty("eventId").ValueKind == JsonValueKind.Null &&
              appearance.GetProperty("actorId").GetString() == "member" &&
              appearance.GetProperty("personal").GetProperty("themeId").ValueKind == JsonValueKind.Null,
            "appearance context returns current actor and only their personal layer");
        Check(appearance.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(
                new[] { "canManageEvent", "canManageSite", "catalog", "event", "personal", "resolved", "site", "actorId" }.Order()),
            "appearance JSON has the declared contract fields");
        Check(appearance.GetProperty("catalog").GetArrayLength() >= 10 &&
              appearance.GetProperty("catalog")[0].EnumerateObject().Select(x => x.Name).Order().SequenceEqual(
                  new[] { "accent", "background", "builtIn", "id", "name", "rowVersion", "surface", "text" }.Order()),
            "catalog JSON contains only typed safe theme fields");
    }

    using (var repeatedExpected = await Post("theme-personal",
               Expected("member") + "&" + Expected("member"), "{", "member", memberCsrf, includeToken: false))
        Check(repeatedExpected.StatusCode == HttpStatusCode.BadRequest, "expectedActor must occur exactly once");
    using (var missingExpected = await Post("theme-personal", "", "{", "member", memberCsrf, includeToken: false))
        Check(missingExpected.StatusCode == HttpStatusCode.BadRequest, "expectedActor is required on every theme POST");
    using (var emptyExpected = await Post("theme-personal", "expectedActor=", "{", "member", memberCsrf, includeToken: false))
        Check(emptyExpected.StatusCode == HttpStatusCode.BadRequest, "expectedActor cannot be empty");
    using (var longExpected = await Post("theme-personal", Expected(new string('x', 129)), "{", "member", memberCsrf, includeToken: false))
        Check(longExpected.StatusCode == HttpStatusCode.BadRequest, "expectedActor length is bounded");
    using (var controlExpected = await Post("theme-personal", Expected("member\u0001"), "{", "member", memberCsrf, includeToken: false))
        Check(controlExpected.StatusCode == HttpStatusCode.BadRequest, "expectedActor rejects control characters");
    using (var mismatch = await Post("theme-personal", Expected("manager"), "{", "member", memberCsrf, includeToken: false))
        Check(mismatch.StatusCode == HttpStatusCode.Forbidden, "cookie/expectedActor mismatch is denied before malformed body or CSRF");
    using (var wrongOrigin = await Post("theme-personal", Expected("member"), """{"themeId":null,"mode":null,"rowVersion":0}""",
               "member", memberCsrf, includeOrigin: false))
        Check(wrongOrigin.StatusCode == HttpStatusCode.Forbidden, "theme POST requires the shared same-origin guard");
    using (var missingCsrf = await Post("theme-personal", Expected("member"), """{"themeId":null,"mode":null,"rowVersion":0}""",
               "member", memberCsrf, includeToken: false))
        Check(missingCsrf.StatusCode == HttpStatusCode.BadRequest, "theme POST requires shared antiforgery validation");
    using (var unknownJson = await Post("theme-personal", Expected("member"),
               """{"themeId":null,"mode":null,"rowVersion":0,"actorId":"manager"}""", "member", memberCsrf))
        Check(unknownJson.StatusCode == HttpStatusCode.BadRequest, "theme JSON rejects unmapped actor override fields");
    using (var missingJson = await Post("theme-personal", Expected("member"),
               """{"themeId":null,"mode":null}""", "member", memberCsrf))
        Check(missingJson.StatusCode == HttpStatusCode.BadRequest, "theme JSON requires all preference fields");
    using (var wrongJsonType = await Post("theme-personal", Expected("member"),
               """{"themeId":null,"mode":null,"rowVersion":"0"}""", "member", memberCsrf))
        Check(wrongJsonType.StatusCode == HttpStatusCode.BadRequest, "theme JSON rejects wrong property types");

    using (var memberSiteDenied = await Post("theme-site", Expected("member"), "{", "member", memberCsrf, includeToken: false))
        Check(memberSiteDenied.StatusCode == HttpStatusCode.Forbidden, "site preauthorization denies non-admin before body");
    using (var managerCatalogDenied = await Post("theme-catalog-save", Expected("manager"),
               "{", "manager", managerCsrf, includeToken: false))
        Check(managerCatalogDenied.StatusCode == HttpStatusCode.Forbidden, "catalog preauthorization denies event manager before body");
    using (var memberEventDenied = await Post("theme-event", Id(visibleEventId) + "&" + Expected("member"),
               "{", "member", memberCsrf, includeToken: false))
        Check(memberEventDenied.StatusCode == HttpStatusCode.Forbidden, "event preauthorization denies non-manager before body");
    using (var duplicateEventId = await Post("theme-event",
               Id(visibleEventId) + "&" + Id(visibleEventId) + "&" + Expected("manager"), "{", "manager", managerCsrf, includeToken: false))
        Check(duplicateEventId.StatusCode == HttpStatusCode.BadRequest, "event write id must occur exactly once");
    using (var managerEventDeniedHidden = await Post("theme-event",
               Id(hiddenEventId) + "&" + Expected("manager"), """{"themeId":null,"mode":null,"rowVersion":0}""", "manager", managerCsrf))
        Check(managerEventDeniedHidden.StatusCode == HttpStatusCode.OK, "authorized event manager can save hidden-event preference");
    using (var siteSave = await Post("theme-site", Expected("admin"),
               """{"themeId":"blue","mode":0,"rowVersion":0}""", "admin", adminCsrf))
        Check(siteSave.StatusCode == HttpStatusCode.OK, "global administrator can save site preference");

    using (var ownWrite = await Post("theme-personal", Expected("member"),
               """{"themeId":"rose","mode":2,"rowVersion":0}""", "member", memberCsrf))
    {
        Check(ownWrite.StatusCode == HttpStatusCode.OK, "personal preference write succeeds");
        using var json = JsonDocument.Parse(await ownWrite.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("themeId").GetString() == "rose" &&
              json.RootElement.GetProperty("mode").GetInt32() == 2 &&
              json.RootElement.GetProperty("rowVersion").GetInt64() == 1,
            "preference response has typed fields and incremented version");
    }
    using (var persisted = await Get("/api/core/theme-appearance", Cookie("member", memberCsrf)))
    {
        using var json = JsonDocument.Parse(await persisted.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("appearance").GetProperty("personal").GetProperty("themeId").GetString() == "rose" &&
              json.RootElement.GetProperty("appearance").GetProperty("personal").GetProperty("rowVersion").GetInt64() == 1,
            "preference write persists across appearance reads");
    }
    using (var stalePreference = await Post("theme-personal", Expected("member"),
               """{"themeId":"blue","mode":1,"rowVersion":0}""", "member", memberCsrf))
        Check(stalePreference.StatusCode == HttpStatusCode.Conflict, "stale preference version returns 409");
    using (var clearPreference = await Post("theme-personal", Expected("member"),
               """{"themeId":null,"mode":null,"rowVersion":1}""", "member", memberCsrf))
    {
        Check(clearPreference.StatusCode == HttpStatusCode.OK, "nullable preference layer can be cleared");
        using var json = JsonDocument.Parse(await clearPreference.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("themeId").ValueKind == JsonValueKind.Null &&
              json.RootElement.GetProperty("mode").ValueKind == JsonValueKind.Null &&
              json.RootElement.GetProperty("rowVersion").GetInt64() == 2,
            "null preference values persist with incremented version");
    }

    string customId;
    using (var createCatalog = await Post("theme-catalog-save", Expected("admin"),
               """{"name":"HTTP custom","accent":"#010203","background":"#111213","surface":"#212223","text":"#313233","rowVersion":0}""",
               "admin", adminCsrf))
    {
        Check(createCatalog.StatusCode == HttpStatusCode.OK, "administrator can create custom catalog entry");
        using var json = JsonDocument.Parse(await createCatalog.Content.ReadAsStringAsync());
        customId = json.RootElement.GetProperty("id").GetString()!;
        Check(!string.IsNullOrEmpty(customId) && json.RootElement.GetProperty("rowVersion").GetInt64() == 1,
            "catalog create returns server-generated id and version");
    }
    using (var updateCatalog = await Post("theme-catalog-save", Id(customId) + "&" + Expected("admin"),
               """{"name":"HTTP edited","accent":"#010203","background":"#111213","surface":"#212223","text":"#313233","rowVersion":1}""",
               "admin", adminCsrf))
    {
        Check(updateCatalog.StatusCode == HttpStatusCode.OK, "administrator can edit catalog entry");
        using var json = JsonDocument.Parse(await updateCatalog.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("name").GetString() == "HTTP edited" &&
              json.RootElement.GetProperty("rowVersion").GetInt64() == 2,
            "catalog edit returns updated typed projection");
    }
    using (var useCustom = await Post("theme-personal", Expected("member"),
               $$"""{"themeId":"{{customId}}","mode":null,"rowVersion":2}""", "member", memberCsrf))
        Check(useCustom.StatusCode == HttpStatusCode.OK, "custom catalog entry can be referenced by own preference");
    using (var deleteInUse = await Post("theme-catalog-delete", Id(customId) + "&" + Expected("admin"),
               """{"rowVersion":2}""", "admin", adminCsrf))
        Check(deleteInUse.StatusCode == HttpStatusCode.BadRequest, "in-use catalog entry cannot be deleted");
    using (var deleteBuiltIn = await Post("theme-catalog-delete", Id("green") + "&" + Expected("admin"),
               """{"rowVersion":0}""", "admin", adminCsrf))
        Check(deleteBuiltIn.StatusCode == HttpStatusCode.BadRequest, "built-in catalog entry cannot be deleted");
    using (var clearCustom = await Post("theme-personal", Expected("member"),
               $$"""{"themeId":null,"mode":null,"rowVersion":3}""", "member", memberCsrf))
        Check(clearCustom.StatusCode == HttpStatusCode.OK, "referencing preference can be cleared");
    using (var deleteCustom = await Post("theme-catalog-delete", Id(customId) + "&" + Expected("admin"),
               """{"rowVersion":2}""", "admin", adminCsrf))
        Check(deleteCustom.StatusCode == HttpStatusCode.OK, "unused custom catalog entry can be deleted");
    using (var staleDelete = await Post("theme-catalog-delete", Id(customId) + "&" + Expected("admin"),
               """{"rowVersion":2}""", "admin", adminCsrf))
        Check(staleDelete.StatusCode == HttpStatusCode.Conflict, "stale catalog delete returns 409");

    using (var missingDeleteId = await Post("theme-catalog-delete", Expected("admin"),
               "{", "admin", adminCsrf, includeToken: false))
        Check(missingDeleteId.StatusCode == HttpStatusCode.BadRequest, "catalog delete requires an id before body parsing");
    using (var duplicateDeleteId = await Post("theme-catalog-delete",
               Id("green") + "&" + Id("green") + "&" + Expected("admin"), "{", "admin", adminCsrf, includeToken: false))
        Check(duplicateDeleteId.StatusCode == HttpStatusCode.BadRequest, "catalog delete id must occur exactly once");

    await using (var seed = app.Services.CreateAsyncScope())
    {
        var factory = seed.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.RevokedAuthSessions.Add(new RevokedAuthSession
        {
            TokenHash = SemEndpoints.Hash("member-revoked-session"),
            UserId = "member",
            RevokedAt = "2026-10-15T08:00:00Z"
        });
        await db.SaveChangesAsync();
    }
    using (var noCookie = await Get("/api/core/theme-appearance", null))
        Check(noCookie.StatusCode == HttpStatusCode.Forbidden, "missing cookie session is denied");
    using (var staleStamp = await Get("/api/core/theme-appearance", "test-auth=member-stale"))
        Check(staleStamp.StatusCode == HttpStatusCode.Forbidden, "stale security stamp is denied");
    using (var missingSession = await Get("/api/core/theme-appearance", "test-auth=member-nosession"))
        Check(missingSession.StatusCode == HttpStatusCode.Forbidden, "missing session stamp is denied");
    using (var revokedSession = await Get("/api/core/theme-appearance", "test-auth=member-revoked"))
        Check(revokedSession.StatusCode == HttpStatusCode.Forbidden, "revoked session is denied");
    using (var stalePost = await Post("theme-personal", Expected("member"),
               "{", "member", memberCsrf, includeToken: false, cookieOverride: "test-auth=member-stale"))
        Check(stalePost.StatusCode == HttpStatusCode.Forbidden, "stale cookie is rejected before body parsing");
    using (var revokedPost = await Post("theme-personal", Expected("member"),
               "{", "member", memberCsrf, includeToken: false, cookieOverride: "test-auth=member-revoked"))
        Check(revokedPost.StatusCode == HttpStatusCode.Forbidden, "revoked cookie is rejected before body parsing");

    var beforeRead = await ThemeSnapshot(app.Services);
    using (var readOnly = await Get("/api/core/theme-appearance?" + Id(visibleEventId), Cookie("admin", adminCsrf)))
        Check(readOnly.StatusCode == HttpStatusCode.OK, "read-only appearance endpoint succeeds");
    Check(beforeRead == await ThemeSnapshot(app.Services), "appearance reads do not change preference, catalog, or audit rows");

    var oversized = new string('x', 65_537);
    using (var tooLarge = await Post("theme-personal", Expected("member"),
               """{"themeId":null,"mode":null,"rowVersion":0,"padding":" """ + oversized + """ "}""",
               "member", memberCsrf))
        Check(tooLarge.StatusCode == HttpStatusCode.RequestEntityTooLarge, "declared body over 65 KiB returns 413");
    using (var streamedTooLarge = await Post("theme-personal", Expected("member"),
               """{"themeId":null,"mode":null,"rowVersion":0,"padding":" """ + oversized + """ "}""",
               "member", memberCsrf, chunked: true))
        Check(streamedTooLarge.StatusCode == HttpStatusCode.RequestEntityTooLarge, "chunked body over 65 KiB returns 413");

    Console.WriteLine($"PASS total={checks}");
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
}

async Task<string> ThemeSnapshot(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    return JsonSerializer.Serialize(new
    {
        Catalog = await db.ThemeCatalogEntries.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Personal = await db.UserThemePreferences.AsNoTracking().OrderBy(x => x.UserId).ToArrayAsync(),
        Events = await db.EventThemePreferences.AsNoTracking().OrderBy(x => x.EventId).ToArrayAsync(),
        Sites = await db.SiteThemePreferences.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Audits = await db.Audits.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
    });
}

ClaimsPrincipal Who(string id) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, id),
    new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim, id + "-session")
], "SyntheticCookie"));

sealed class SyntheticCookieHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Cookies["test-auth"];
        if (string.IsNullOrEmpty(value)) return Task.FromResult(AuthenticateResult.NoResult());
        var stale = value.EndsWith("-stale", StringComparison.Ordinal);
        var noSession = value.EndsWith("-nosession", StringComparison.Ordinal);
        var revoked = value.EndsWith("-revoked", StringComparison.Ordinal);
        var suffixLength = stale ? 6 : noSession ? 10 : revoked ? 8 : 0;
        var id = suffixLength == 0 ? value : value[..^suffixLength];
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id),
            new("AspNet.Identity.SecurityStamp", stale ? "invalid-stamp" : id + "-stamp")
        };
        if (!noSession) claims.Add(new(PasskeyAdditionService.SessionClaim, revoked ? id + "-revoked-session" : id + "-session"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}

sealed class NoopPushTransport : IEventPushTransport
{
    public bool Configured => false;
    public Task Send(PushDevice device, EventPushPayload payload, int ttl, CancellationToken ct) => Task.CompletedTask;
}

sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
    public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
}