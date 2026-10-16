using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Data;
namespace Sem.Web.Runtime;

// Stamp rotation invalidates every previous cookie even after the account is re-enabled.
// Used on HTTP requests and by the interactive-circuit revalidation provider.
public static class LiveAuthenticationSession
{
    public static async Task<bool> ValidAsync(ApplicationDbContext db, ClaimsPrincipal principal,
        IdentityOptions options, CancellationToken ct = default)
    {
        var id = principal.FindFirstValue(options.ClaimsIdentity.UserIdClaimType);
        var stamp = principal.FindFirstValue(options.ClaimsIdentity.SecurityStampClaimType);
        var session = principal.FindFirstValue(PasskeyAdditionService.SessionClaim);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(stamp) || string.IsNullOrWhiteSpace(session)) return false;
        var user = await db.Users.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Enabled, x.EmailConfirmed, x.SecurityStamp }).SingleOrDefaultAsync(ct);
        return user is { Enabled: true, EmailConfirmed: true } && user.SecurityStamp == stamp &&
            !await db.RevokedAuthSessions.AsNoTracking().AnyAsync(x => x.TokenHash == SemEndpoints.Hash(session), ct);
    }
    public static IApplicationBuilder UseSemLiveAuthenticationSession(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true &&
                !await ValidAsync(context.RequestServices.GetRequiredService<ApplicationDbContext>(), context.User,
                    context.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value, context.RequestAborted))
            {
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
            await next();
        });
}
