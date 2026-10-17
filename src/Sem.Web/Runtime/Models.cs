using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public class SiteState { public int Id { get; set; } = 1; public string Name { get; set; } = "SEM"; public string? PendingAdminId { get; set; } public long PendingUntil { get; set; } public bool Initialized { get; set; } }
public class Invitation { public string Id { get; set; } = Guid.NewGuid().ToString(); public string TokenHash { get; set; } = ""; public string OwnerId { get; set; } = ""; public string IssuerId { get; set; } = ""; public string? RequestId { get; set; } public int Limit { get; set; } public int Uses { get; set; } public long Expires { get; set; } public long Created { get; set; } public bool Disabled { get; set; } public string? RevokedBy { get; set; } public string? RevocationReason { get; set; } public long? RevokedAt { get; set; } public string ProtectedToken { get; set; } = ""; public string? Purpose { get; set; } public string? IssuanceSource { get; set; } }
public class InvitationRequest { public string Id { get; set; } = Guid.NewGuid().ToString(); public string UserId { get; set; } = ""; public string Reason { get; set; } = ""; public int Count { get; set; } = 1; public int Days { get; set; } = 1; public string Status { get; set; } = "Pending"; public string? DecidedBy { get; set; } public long Created { get; set; } public long? DecidedAt { get; set; } public string? DecisionComment { get; set; } }
public class EmailTicket { public string Id { get; set; } = Guid.NewGuid().ToString(); public string UserId { get; set; } = ""; public string Hash { get; set; } = ""; public long Expires { get; set; } public bool Used { get; set; } }
public class Message { public string Id { get; set; } = Guid.NewGuid().ToString(); public string UserId { get; set; } = ""; public string Title { get; set; } = ""; public string Body { get; set; } = ""; public long Created { get; set; } public long? ReadAt { get; set; } }
public class Delivery { public string Id { get; set; } = Guid.NewGuid().ToString(); public string UserId { get; set; } = ""; public string Kind { get; set; } = "Email"; public string Subject { get; set; } = ""; public string ProtectedBody { get; set; } = ""; public long Due { get; set; } public int Attempts { get; set; } public long? SentAt { get; set; } public string? Error { get; set; } }
public class PushDevice { public string? SecurityStamp {get;set;} public string? SessionHash {get;set;}  public string Id { get; set; } = Guid.NewGuid().ToString(); public string UserId { get; set; } = ""; public string Endpoint { get; set; } = ""; public string P256dh { get; set; } = ""; public string Auth { get; set; } = ""; }
public class TestEvent { public string Id { get; set; } = Guid.NewGuid().ToString(); public string Title { get; set; } = ""; public string ManagerId { get; set; } = ""; public long Starts { get; set; } public long? Opens { get; set; } public int Capacity { get; set; } = 20; public string Phase { get; set; } = "Hidden"; }
public class Participation { public string Id { get; set; } = Guid.NewGuid().ToString(); public string EventId { get; set; } = ""; public string UserId { get; set; } = ""; }
public class Audit { public string Id { get; set; } = Guid.NewGuid().ToString(); public string ActorId { get; set; } = ""; public string Action { get; set; } = ""; public string EntityId { get; set; } = ""; public long At { get; set; } }
public class PasskeyAddition
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = "";
    public string SessionHash { get; set; } = "";
    public string Email { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public string? ReauthenticationChallengeHash { get; set; }
    public string? CreationChallengeHash { get; set; }
    public string? EmailTokenHash { get; set; }
    public long Created { get; set; }
    public long Expires { get; set; }
    public long? ReauthenticatedAt { get; set; }
    public long? EmailVerifiedAt { get; set; }
    public long? RegistrationUntil { get; set; }
    public long? CompletedAt { get; set; }
    public bool Cancelled { get; set; }
}
public class SemUserConfirmation : IUserConfirmation<ApplicationUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user) => Task.FromResult(user.EmailConfirmed && user.Enabled);
}
