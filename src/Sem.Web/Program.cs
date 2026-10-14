using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Sem.Web.Components;
using Sem.Web.Components.Account;
using Sem.Web.Data;
using Sem.Web.Runtime;
using WebPush;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Sem.Web.Core;

// Maintenance exits before deployment configuration, migrations, HTTP or notification workers.
if (args.Length > 0 && args[0] == "--legacy-event-reset")
{
    Environment.ExitCode = await LegacyEventResetMaintenance.RunAsync(args[1..]);
    return;
}
if (args.Contains("--generate-vapid"))
{
    var keys = VapidHelper.GenerateVapidKeys();
    Console.WriteLine("PUSH_PUBLIC_KEY=" + keys.PublicKey);
    Console.WriteLine("PUSH_PRIVATE_KEY=" + keys.PrivateKey);
    return;
}
var builder = WebApplication.CreateBuilder(args);
var deployment = builder.AddSemDeployment();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(o => { o.DefaultScheme = IdentityConstants.ApplicationScheme; o.DefaultSignInScheme = IdentityConstants.ExternalScheme; }).AddIdentityCookies();
builder.Services.AddAuthentication().AddCookie("SEM.Enrollment", o =>
{
    o.Cookie.Name = "__Host-SEM-Enrollment"; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.Cookie.HttpOnly = true; o.Cookie.Path = "/"; o.Cookie.SameSite = SameSiteMode.Lax; o.ExpireTimeSpan = TimeSpan.FromMinutes(20); o.SlidingExpiration = false;
});
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "__Host-SEM-CSRF"; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.Cookie.Path = "/"; o.Cookie.HttpOnly = true; });
builder.Services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(deployment.DataDirectory, "sem.db")}"), ServiceLifetime.Scoped);
builder.Services.AddScoped<CoreFoundationService>();
builder.Services.AddIdentityCore<ApplicationUser>(o => { o.SignIn.RequireConfirmedAccount = true; o.User.RequireUniqueEmail = true; o.Stores.SchemaVersion = IdentitySchemaVersions.Version3; }).AddEntityFrameworkStores<ApplicationDbContext>().AddSignInManager().AddDefaultTokenProviders().AddUserConfirmation<SemUserConfirmation>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<BusinessNotificationStore>();
builder.Services.AddSingleton<IBusinessEmailTransport,BusinessEmailTransport>();
builder.Services.AddScoped<BusinessEmailDispatcher>();
builder.Services.AddHostedService<BusinessEmailWorker>();
builder.Services.AddScoped<DailyDigestScheduler>();
builder.Services.AddScoped<DailyDigestDispatcher>();
builder.Services.AddHostedService<DailyDigestWorker>();
builder.Services.AddScoped<NotificationReservationReconciler>();
builder.Services.AddHostedService<NotificationReservationWorker>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PasskeyAdditionService>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<PhaseWorker>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
    {
        // Category must be part of the partition key: otherwise the first request
        // chooses a shared API/assets limit and an API burst can block Blazor scripts.
        var api = c.Request.Path.StartsWithSegments("/api");
        var key = (api ? "api:" : "web:") + (c.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = api ? 60 : 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});
var app = builder.Build();
app.UseForwardedHeaders();
await app.InitializeSemStorageAsync();
app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.Equals("/workspace", StringComparison.OrdinalIgnoreCase)) context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    if (!context.Request.Path.StartsWithSegments("/health"))
    {
        if (!string.Equals(context.Request.Host.Host, new Uri(deployment.PublicUrl).Host, StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 400; return; }
        if (!context.Request.IsHttps) { context.Response.StatusCode = 400; await context.Response.WriteAsync("HTTPS forwarding is not configured correctly."); return; }
        // Never expose generated password or open registration routes.
        if (context.Request.Path.StartsWithSegments("/Account", StringComparison.OrdinalIgnoreCase) || context.Request.Path == "/counter" || context.Request.Path == "/weather") { context.Response.StatusCode = 404; return; }
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/_blazor") &&
        context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin.ToString() != deployment.PublicUrl.TrimEnd('/'))
    { context.Response.StatusCode = 403; return; }
    var session = context.User.FindFirstValue(PasskeyAdditionService.SessionClaim);
    if (session is not null)
    {
        var db = context.RequestServices.GetRequiredService<ApplicationDbContext>();
        var hash = SemEndpoints.Hash(session);
        if (await db.RevokedAuthSessions.AnyAsync(s => s.TokenHash == hash))
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
    }
    await next();
});
app.UseAuthorization();
app.UseAntiforgery();
// Standard wwwroot fallback for SDK/runtime variants whose resource collection
// emits an unhashed framework URL. Keep it after all origin/HTTPS/rate guards.
app.UseStaticFiles();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (ApplicationDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "storage-ready" }) : Results.StatusCode(503));
app.MapSem();
app.MapCoreFoundation();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
