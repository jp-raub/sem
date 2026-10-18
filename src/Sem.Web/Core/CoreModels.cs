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
    public string Icon { get; set; } = "";
    public long RowVersion { get; set; }
}
public sealed class ProfileQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = "";
    public string Type { get; set; } = "Text";
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
    public long RowVersion { get; set; }
}
public sealed class ProfileAnswer
{
    public string UserId { get; set; } = "";
    public string QuestionId { get; set; } = "";
    public string Value { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}
public sealed class ProfileQrCredential
{
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string IssuedAt { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public long RowVersion { get; set; }
}
public sealed class SessionEvent
{
    public string Icon { get; set; } = "";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string DescriptionFormat { get; set; } = "text";
    public string Location { get; set; } = "";
    public string LocationUrl { get; set; } = "";
    public string ScheduledDate { get; set; } = "";
    public string? OpensAt { get; set; }
    public string AnnouncementMessage { get; set; } = "";
    public int DefaultPrepSeconds { get; set; } = 600;
    public int? Capacity { get; set; }
    public bool RoundStarts { get; set; } = true;
    public string ClockFormat { get; set; } = "seconds";
    public string DurationFormat { get; set; } = "units";
    public string SongIcon { get; set; } = "🎵";
    public string? PlannedEndAt { get; set; }
    // Compatibility visibility cache; the active Phases row is authoritative for phase kind.
    public string PhaseKind { get; set; } = "Hidden";
    public bool ManualPhaseOverride { get; set; }
    // Server-only import guard state. Existing events remain unknown after migration.
    public bool ImportHistoryKnown { get; set; }
    public bool HasEverRegistrationPhase { get; set; }
    public List<EventPhase> Phases { get; set; } = [];
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

/// <summary>Per-user normalized image and independent optimistic-concurrency version.</summary>
public sealed class ProfileImage
{
    public string UserId { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public long RowVersion { get; set; }
}
