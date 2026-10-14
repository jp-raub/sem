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
builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(deployment.DataDirectory, "sem.db")}"));
builder.Services.AddIdentityCore<ApplicationUser>(o => { o.SignIn.RequireConfirmedAccount = true; o.User.RequireUniqueEmail = true; o.Stores.SchemaVersion = IdentitySchemaVersions.Version3; }).AddEntityFrameworkStores<ApplicationDbContext>().AddSignInManager().AddDefaultTokenProviders().AddUserConfirmation<SemUserConfirmation>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PasskeyAdditionService>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = c.Request.Path.StartsWithSegments("/api") ? 60 : 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseForwardedHeaders();
await app.InitializeSemStorageAsync();
app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
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
app.UseAuthorization();
app.UseAntiforgery();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (ApplicationDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "storage-ready" }) : Results.StatusCode(503));
app.MapSem();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
