namespace Sem.Web.Core;

public sealed record ManualNotificationComposerContext(string UserId, string EventId, string Title);

// Circuit-memory only. Never serialise this draft or pending request into browser storage.
public sealed class ManualNotificationComposerState
{
    public string Subject { get; private set; } = "";
    public string Body { get; private set; } = "";
    public ManualNotificationPreview? Preview { get; private set; }
    public ManualNotificationSubmit? Pending { get; private set; }
    public ManualNotificationAcceptance? Accepted { get; private set; }
    public bool Confirmed { get; private set; }
    public bool Busy { get; private set; }
    public bool Uncertain { get; private set; }
    public bool NavigationBlocked => Busy || Pending is not null || Preview is not null || Subject.Length > 0 || Body.Length > 0;
    public bool CanEdit => !Busy && Pending is null && Accepted is null;

    public void Edit(string subject, string body)
    {
        if (!CanEdit) throw new InvalidOperationException();
        Subject = subject; Body = body; Preview = null; Confirmed = false;
    }
    public void BeginPreview()
    {
        if (!CanEdit) throw new InvalidOperationException();
        Preview = null; Confirmed = false; Busy = true;
    }
    public void ShowPreview(ManualNotificationPreview preview)
    {
        if (!Busy || Pending is not null || Accepted is not null) throw new InvalidOperationException();
        Subject = preview.Subject; Body = preview.Body; Preview = preview; Busy = false;
    }
    public void PreviewFailed() { if (Pending is not null) throw new InvalidOperationException(); Busy = false; Preview = null; Confirmed = false; }
    public void Confirm(bool value)
    {
        if (!CanEdit || Preview is null) throw new InvalidOperationException();
        Confirmed = value;
    }
    public ManualNotificationSubmit BeginSubmit()
    {
        if (Busy || Accepted is not null) throw new InvalidOperationException();
        if (Pending is null) {
            if (Preview is null || !Confirmed) throw new InvalidOperationException();
            Pending = new(Guid.NewGuid().ToString("D"), Preview.Fingerprint, Preview.Subject, Preview.Body, true);
        }
        Busy = true;
        return Pending;
    }
    public void SubmissionUnknown() { if (Pending is null) throw new InvalidOperationException(); Busy = false; Uncertain = true; }
    public void SubmissionRejected()
    {
        if (Pending is null) throw new InvalidOperationException();
        Busy = false;
        // A rejection of a retry does NOT disprove that an earlier attempt committed.
        if (Uncertain) return;
        Pending = null; Preview = null; Confirmed = false;
    }
    public void SubmissionAccepted(ManualNotificationAcceptance result)
    {
        if (Pending is null || result.RequestId != Pending.RequestId || result.RecipientCount < 1 || result.AcceptedAt < 1 || result.AcceptedAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            throw new InvalidOperationException("Mismatched acceptance response");
        Accepted = result; Pending = null; Preview = null; Subject = Body = ""; Confirmed = Busy = Uncertain = false;
    }
    public void DiscardDraft()
    {
        if (!CanEdit) throw new InvalidOperationException();
        Subject = Body = ""; Preview = null; Confirmed = false;
    }
    public void StartNew()
    {
        if (Busy || Pending is not null) throw new InvalidOperationException();
        DiscardAccepted();
    }
    private void DiscardAccepted() { Accepted = null; Subject = Body = ""; Preview = null; Confirmed = Uncertain = false; }
}
