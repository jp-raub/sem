using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sem.Web.Data;
namespace Sem.Authentication;

public static class AuthenticationPipeline
{
    public const string ProtectionApplicationName = "SEM.ExternalTest";
    public const string LoginPolicy = "sem-native-login";
    public static IServiceCollection AddSemNativeAuthentication(this IServiceCollection services, AuthenticationSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LoginAttemptStore>();
        services.AddDataProtection().SetApplicationName(ProtectionApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(settings.KeysDirectory));
        services.Configure<ForwardedHeadersOptions>(o => {
            o.ForwardedHeaders = settings.TrustedProxies.Count == 0 ? ForwardedHeaders.None : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
            foreach (var proxy in settings.TrustedProxies) o.KnownProxies.Add(proxy);
        });
        services.AddAuthentication(o => {
            o.DefaultScheme = IdentityConstants.ApplicationScheme;
            o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        }).AddIdentityCookies();
        services.AddIdentityCore<ApplicationUser>(o => {
            o.SignIn.RequireConfirmedAccount = true;
            o.User.RequireUniqueEmail = true;
            o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        }).AddEntityFrameworkStores<ApplicationDbContext>().AddSignInManager()
          .AddDefaultTokenProviders().AddUserConfirmation<ActiveUserConfirmation>();
        services.Configure<IdentityPasskeyOptions>(o => {
            o.ServerDomain = settings.RelyingPartyId;
            o.UserVerificationRequirement = "required";
        });
        services.ConfigureApplicationCookie(o => {
            o.Cookie.Name = "__Host-SEM-Auth";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.HttpOnly = true; o.Cookie.Path = "/"; o.Cookie.SameSite = SameSiteMode.Lax;
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = async c => {
                var db = c.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
                var options = c.HttpContext.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value;
                if (c.Principal is null || !await AuthenticationSession.ValidAsync(db, c.Principal, options, c.HttpContext.RequestAborted)) {
                    c.RejectPrincipal();
                    await c.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    return;
                }
                await SecurityStampValidator.ValidatePrincipalAsync(c);
            };
        });
        services.AddOptions<SecurityStampValidatorOptions>().Configure<IOptions<IdentityOptions>>((o, identity) =>
            o.OnRefreshingPrincipal = c => {
                AuthenticationSession.PreserveBinding(c.CurrentPrincipal, c.NewPrincipal, identity.Value);
                return Task.CompletedTask;
            });
        services.AddAntiforgery(o => {
            o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "__Host-SEM-CSRF";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.Cookie.HttpOnly = true; o.Cookie.Path = "/";
        });
        services.AddRateLimiter(o => {
            o.RejectionStatusCode = 429;
            o.AddPolicy(LoginPolicy, c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
                PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));
        });
        services.AddAuthorization();
        return services;
    }
    public static IApplicationBuilder UseSemNativeAuthentication(this IApplicationBuilder app)
    {
        app.UseForwardedHeaders();
        app.Use(async (c, next) => {
            c.Response.Headers.CacheControl = "no-store";
            c.Response.Headers.Pragma = "no-cache";
            c.Response.Headers["Referrer-Policy"] = "no-referrer";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var settings = c.RequestServices.GetRequiredService<AuthenticationSettings>();
            // プロキシ未指定時に転送ヘッダーを信頼しない。実効Hostとportも検証。
            if (!c.Request.IsHttps || !string.Equals("https://" + c.Request.Host.ToUriComponent(), settings.PublicOrigin, StringComparison.OrdinalIgnoreCase)) {
                c.Response.StatusCode = 400; return;
            }
            await next();
        });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
    public static IEndpointRouteBuilder MapSemNativeAuthentication(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/auth/csrf", (HttpContext c, IAntiforgery csrf) => {
            if (c.Request.QueryString.HasValue) return Results.BadRequest();
            return Results.Json(new {token = csrf.GetAndStoreTokens(c).RequestToken});
        }).RequireRateLimiting(LoginPolicy);
        endpoints.Map("/api/auth/{action}", async (HttpContext c, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signin, IAntiforgery csrf, AuthenticationSettings settings) => {
            try { await ApplicationLoginEndpoints.Handle(c, users, signin, csrf, settings); }
            catch (Exception error) when (error is ValidationException or JsonException or AntiforgeryValidationException or UnauthorizedAccessException) {
                c.Response.StatusCode = error is UnauthorizedAccessException ? 401 : 400;
                await c.Response.WriteAsJsonAsync(new {error="ログインできませんでした。ページを再読み込みしてお試しください。"}, c.RequestAborted);
            }
        }).RequireRateLimiting(LoginPolicy);
        endpoints.MapGet("/api/auth/session", async (HttpContext c, ApplicationDbContext db, IOptions<IdentityOptions> options) => {
            if (c.Request.QueryString.HasValue) return Results.BadRequest();
            if (!await AuthenticationSession.ValidAsync(db, c.User, options.Value, c.RequestAborted)) return Results.Unauthorized();
            var id = AuthenticationSession.SingleClaim(c.User, options.Value.ClaimsIdentity.UserIdClaimType)!;
            return Results.Json(await db.Users.AsNoTracking().Where(u => u.Id == id)
                .Select(u => new {u.Id, u.DisplayName, u.IsAdmin}).SingleAsync(c.RequestAborted));
        });
        return endpoints;
    }
    sealed class ActiveUserConfirmation : IUserConfirmation<ApplicationUser>
    {
        public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user) => Task.FromResult(user.Enabled && user.EmailConfirmed);
    }
}
