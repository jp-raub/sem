using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Components;
using Sem.Web.Components.Account;
using Sem.Web.Data;
using Sem.Web.Runtime;

var builder = WebApplication.CreateBuilder(args);
var deployment = builder.AddSemDeployment();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = IdentityConstants.ApplicationScheme;
    o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
}).AddIdentityCookies();
builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(deployment.DataDirectory, "sem.db")}"));
builder.Services.AddIdentityCore<ApplicationUser>(o =>
{
    o.SignIn.RequireConfirmedAccount = true;
    o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
}).AddEntityFrameworkStores<ApplicationDbContext>().AddSignInManager().AddDefaultTokenProviders();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
var app = builder.Build();
app.UseForwardedHeaders();
await app.InitializeSemStorageAsync();

// Fail closed until initialization, passkey-only enrollment and notifications are implemented.
// The official template's open registration/password pages MUST NOT be externally reachable.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health/live" || context.Request.Path == "/health/ready")
    {
        await next();
        return;
    }
    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    context.Response.Headers.CacheControl = "no-store";
    await context.Response.WriteAsync("SEM implementation in progress. External test is not available yet.");
});
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", externalTestReady = false }));
app.MapGet("/health/ready", async (ApplicationDbContext db) =>
{
    return await db.Database.CanConnectAsync()
        ? Results.Ok(new { status = "storage-ready", externalTestReady = false })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});
app.Run();
