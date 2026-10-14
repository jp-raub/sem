namespace Sem.Web.Core;
// Explicit-read circuit state only; never saved to browser storage or used as authorization.
public sealed class ManualNotificationPushStatusViewState
{
    public sealed record Ticket(long Version, string EventId, string ActorId, ManualNotificationAcceptance Acceptance);
    private long version; private Ticket? current;
    public bool Busy => current is not null;
    public ManualNotificationPushStatus? View { get; private set; }
    public Ticket Begin(string eventId, string actorId, ManualNotificationAcceptance accepted)
    {
        if (Busy || string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(actorId) || accepted.RecipientCount < 1 ||
            accepted.AcceptedAt < 1 || accepted.AcceptedAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds() ||
            !Guid.TryParseExact(accepted.RequestId,"D",out var id) || id.ToString("D") != accepted.RequestId) throw new InvalidOperationException();
        View = null; return current = new(checked(++version),eventId,actorId,accepted);
    }
    public bool Complete(Ticket ticket, ManualNotificationPushStatus status)
    {
        if (current != ticket) return false;
        var d=status.PersistedDeliveryCounts;
        if (status.RequestId != ticket.Acceptance.RequestId || status.EventId != ticket.EventId || status.RecipientCount != ticket.Acceptance.RecipientCount || status.AcceptedAt != ticket.Acceptance.AcceptedAt ||
            status.AdmissionGate is not ("NotAdmitted" or "Stopped" or "Expired" or "Eligible") ||
            (status.AdmissionGate=="NotAdmitted" ? status.ExpiresAt is not null : status.ExpiresAt != status.AcceptedAt+600 || status.ExpiresAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) ||
            status.PreparedRecipients<0 || status.UnpreparedRecipients<0 || status.NoEligibleDevices<0 || status.OtherSuppressedPreparations<0 ||
            (long)status.PreparedRecipients+status.UnpreparedRecipients+status.NoEligibleDevices+status.OtherSuppressedPreparations != status.RecipientCount ||
            d is null || new[]{d.Pending,d.Claimed,d.Accepted,d.Unknown,d.Expired,d.Suppressed,d.Failed}.Any(x=>x<0)) throw new InvalidOperationException("Mismatched status snapshot");
        View=status;current=null;return true;
    }
    public bool Failed(Ticket ticket){if(current!=ticket)return false;current=null;View=null;return true;}
    public void Reset(){checked{version++;}current=null;View=null;}
}
