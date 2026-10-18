using System.Security.Claims;
namespace Sem.Web.Core;
public sealed record EventTabsView(string UserId, string EventId, string Title, string Description, string Location, string ScheduledDate, string PhaseName, string[] Available, string LocationUrl = "", string DescriptionFormat = "text", string? HeaderImageDataUrl = null, string Icon = "");
public sealed partial class CoreFoundationService
{
    public async Task<EventTabsView> ReadEventTabsAsync(ClaimsPrincipal principal, string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (e, actor, phase, manager) = await ParticipantEvent(db, principal, id, ct);
        var tabs = new List<string> { "main", "roster" };
        if (manager) tabs.Add("checkin");
        if (ProgressCalculator.IsEventDay(e.ScheduledDate, clock.GetUtcNow())) tabs.Add("progress");
        if (manager) tabs.Add("notifications");
        var header = EventHeaderImageNormalizer.DataUrl(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(db.EventHeaderImages.Where(x => x.EventId == id), ct));
        await tx.CommitAsync(ct);
        return new(actor.Id, id, e.Title, e.Description, e.Location, e.ScheduledDate, phase.Name, tabs.ToArray(), e.LocationUrl, e.DescriptionFormat, header, e.Icon);
    }
}
