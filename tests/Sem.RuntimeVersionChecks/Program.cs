using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Components.Pages;
using Sem.Web.Data;
using Sem.Web.Runtime;

var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
AssemblyMetadataAttribute[] Meta(string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string run = "123", string attempt = "2") =>
    [new("SemCommit", sha), new("SemPublicationRun", run), new("SemPublicationAttempt", attempt)];
var valid = RuntimeBuildInfo.FromMetadata(Meta());
Check(valid.Published && valid.Commit == new string('a', 40) && valid.BuildId == "run-123-2", "Full SHA and unique publication identity");
Check(valid.Display == "aaaaaaaa / run-123-2", "Human-readable short version");
Check(valid.SourceUrl == "https://github.com/jp-raub/sem/commit/" + new string('a',40), "Source link bound to exact version");
Check(RuntimeBuildInfo.FromMetadata(Meta(attempt:"3")).BuildId != valid.BuildId, "Same commit different build distinguished");
foreach (var bad in new[] { Meta(sha:"main"), Meta(sha:new string('A',40)), Meta(sha:new string('a',40)+"\n"), Meta(run:"0"), Meta(attempt:"-1"), Meta(run:"1\n"), Meta(run:"999999999999999999999"), Meta(attempt:"") })
{
    var info = RuntimeBuildInfo.FromMetadata(bad);
    Check(!info.Published && info.Commit is null && info.BuildId is null && info.SourceUrl is null, "Invalid identity becomes explicitly unknown");
}
Check(!RuntimeBuildInfo.FromMetadata([]).Published, "Missing metadata is development, not a published version");
Check(!RuntimeBuildInfo.FromMetadata(Meta().Concat([new AssemblyMetadataAttribute("SemCommit",new string('a',40))])).Published, "Duplicate identity rejected");
var before = JsonSerializer.Serialize(RuntimeBuildInfo.Current.Payload(null));
Environment.SetEnvironmentVariable("SEM_COMMIT", new string('f',40));
Environment.SetEnvironmentVariable("SemPublicationRun", "999999");
Check(JsonSerializer.Serialize(RuntimeBuildInfo.Current.Payload(null)) == before, "Runtime environment cannot override product assembly identity");
Environment.SetEnvironmentVariable("SEM_COMMIT", null);
Environment.SetEnvironmentVariable("SemPublicationRun", null);

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite("Data Source=:memory:"));
var app = builder.Build();
app.MapGet("/api/{**action}", () => Results.StatusCode(418)); // Production-shaped catch-all must not win.
app.MapRuntimeVersion();
try
{
    await app.StartAsync();
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    var probe = new string('b',32);
    foreach (var path in new[] {"/api/version", "/health/ready"})
    {
        using var response = await client.GetAsync(path+"?probe="+probe);
        Check(response.StatusCode == HttpStatusCode.OK, path+" anonymous GET exact route");
        Check(response.Headers.CacheControl?.NoStore == true, path+" is never cacheable");
        Check(response.Content.Headers.ContentType?.MediaType == "application/json", path+" JSON not HTML");
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(data.RootElement.GetProperty("probe").GetString() == probe, path+" binds response to fresh probe");
        foreach (var query in new[] {"?probe=bad", "?probe="+probe+"&probe="+probe, "?other=x", "?probe="+probe+"%0A"})
        {
            using var bad = await client.GetAsync(path+query);
            Check(bad.StatusCode == HttpStatusCode.BadRequest && bad.Headers.CacheControl?.NoStore == true, path+" malformed/ambiguous probe rejected without caching");
        }
        using var noProbe = await client.GetAsync(path);
        Check(noProbe.IsSuccessStatusCode, path+" readable without probe");
        using var post = await client.PostAsync(path, new StringContent(""));
        Check(post.StatusCode == HttpStatusCode.MethodNotAllowed, path+" has no write operation");
    }
    var json = await client.GetStringAsync("/api/version");
    using var version = JsonDocument.Parse(json);
    var keys = version.RootElement.EnumerateObject().Select(p=>p.Name).Order().ToArray();
    Check(keys.SequenceEqual(new[]{"schema","application","channel","commit","buildId","publicationRun","publicationAttempt","probe"}.Order()), "Only explicit non-secret public fields");
    Check(json == before, "HTTP reports the executing product assembly");
}
finally { await app.StopAsync(); await app.DisposeAsync(); }

var services = new ServiceCollection().AddLogging().BuildServiceProvider();
await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<About>(ParameterView.Empty)).ToHtmlString());
Check(WebUtility.HtmlDecode(html).Contains("応答しているアプリの稼働版"), "About page labels the responding runtime, not latest main");
Check(html.Contains("/api/version") && html.Contains("href=\"/\""), "About page has machine and home links");
Check(!html.Contains("<form"), "Version page performs no mutations");
Console.WriteLine($"All {checks} runtime version checks passed.");
