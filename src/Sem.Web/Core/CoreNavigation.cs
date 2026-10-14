using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
// Deliberately excludes email, message bodies, other members and authentication material.
public sealed record NavigationView(string UserId, string DisplayName, string ServiceName, string Description, bool IsAdmin, int UnreadCount);
public sealed partial class CoreFoundationService
{
    public async Task<NavigationView> ReadNavigationAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await Actor(db, principal, ct);
        var settings = await db.ServiceSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var name = settings?.Name ?? await db.SiteStates.Where(s => s.Id == 1).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "SEM";
        var unread = await db.Messages.CountAsync(m => m.UserId == user.Id && m.ReadAt == null, ct);
        return new(user.Id, user.DisplayName, name, settings?.Description ?? "", user.IsAdmin, unread);
    }
}
