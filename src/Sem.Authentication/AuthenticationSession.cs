using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Authentication;

public static class AuthenticationSession
{
    // 旧Cookie・失効テーブルと互換。ハッシュの大文字も変えない。
    public const string SessionClaim = "sem.authentication-session";
    public static string NewSession() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string? SingleClaim(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).ToArray();
        return claims.Length == 1 && !string.IsNullOrWhiteSpace(claims[0].Value) ? claims[0].Value : null;
    }
    public static void PreserveBinding(ClaimsPrincipal? current, ClaimsPrincipal? refreshed, IdentityOptions options)
    {
        if (current?.Identity?.IsAuthenticated != true || refreshed?.Identity is not ClaimsIdentity target) return;
        var session = SingleClaim(current, SessionClaim);
        var idType = options.ClaimsIdentity.UserIdClaimType;
        var stampType = options.ClaimsIdentity.SecurityStampClaimType;
        var id = SingleClaim(current, idType);
        var stamp = SingleClaim(current, stampType);
        if (session is not null && id is not null && stamp is not null &&
            id == SingleClaim(refreshed, idType) && stamp == SingleClaim(refreshed, stampType) &&
            !refreshed.HasClaim(c => c.Type == SessionClaim))
            target.AddClaim(new Claim(SessionClaim, session));
    }
    public static async Task<bool> ValidAsync(ApplicationDbContext db, ClaimsPrincipal principal, IdentityOptions options, CancellationToken ct = default)
    {
        if (principal.Identity?.IsAuthenticated != true) return false;
        var id = SingleClaim(principal, options.ClaimsIdentity.UserIdClaimType);
        var stamp = SingleClaim(principal, options.ClaimsIdentity.SecurityStampClaimType);
        var session = SingleClaim(principal, SessionClaim);
        if (id is null || stamp is null || session is null) return false;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new {u.Enabled, u.EmailConfirmed, u.SecurityStamp}).SingleOrDefaultAsync(ct);
        return user is {Enabled:true, EmailConfirmed:true} && user.SecurityStamp == stamp &&
            !await db.RevokedAuthSessions.AsNoTracking().AnyAsync(s => s.TokenHash == Hash(session), ct);
    }
}
