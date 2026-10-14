namespace Sem.Web.Core;

public sealed class ServiceSettings
{
    public int Id { get; set; } = 1;
    public string Name { get; set; } = "SEM";
    public string Description { get; set; } = "";
    public long RowVersion { get; set; }
}
public sealed class UserProfile
{
    public string UserId { get; set; } = "";
    public string Reading { get; set; } = "";
    public string CallName { get; set; } = "";
    public string SocialJson { get; set; } = "[]";
    public long RowVersion { get; set; }
}
public sealed class SessionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Location { get; set; } = "";
    public string ScheduledDate { get; set; } = "";
    public string? OpensAt { get; set; }
    public string AnnouncementMessage { get; set; } = "";
    // Foundation supports Hidden/View only. Custom phases/enrollment are the next unit.
    public string PhaseKind { get; set; } = "Hidden";
    public string CreatedAt { get; set; } = "";
    public long RowVersion { get; set; }
    public List<EventAdmin> Admins { get; set; } = [];
}
public sealed class EventAdmin
{
    public string EventId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string GrantedAt { get; set; } = "";
}
public sealed class RevokedAuthSession
{
    public string TokenHash { get; set; } = "";
    public string UserId { get; set; } = "";
    public string RevokedAt { get; set; } = "";
}
