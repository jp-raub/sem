namespace Sem.Web.Core;

/// <summary>Memory-only, single-operation state for confirmed event CSV append.</summary>
public sealed class EventImportComposerState
{
    public string Csv { get; private set; } = "";
    public EventImportPreview? Preview { get; private set; }
    public EventImportPending? Pending { get; private set; }
    public EventImportAcceptance? Accepted { get; private set; }
    public bool Confirmed { get; private set; }
    public bool ConfirmOverrun { get; private set; }
    public bool Busy { get; private set; }
    public bool ClientDirty { get; private set; }
    public bool CanEdit => !Busy && Pending is null && Accepted is null;
    public bool NeedsOverrunAcknowledgement => Preview?.OverrunSeconds is > 0;
    public bool CanApply => !Busy && Pending is null && Accepted is null && Preview is { IsValid: true }
        && Confirmed && (!NeedsOverrunAcknowledgement || ConfirmOverrun);
    public bool NavigationBlocked => Accepted is null && (Busy || Pending is not null || Csv.Length > 0 || Preview is not null || ClientDirty);

    public void Edit(string csv)
    {
        if (!CanEdit) return;
        Csv = csv; ClientDirty = false; Preview = null; Confirmed = ConfirmOverrun = false; Accepted = null;
    }
    public void MarkClientDirty(bool dirty) { if (CanEdit) ClientDirty = dirty; }
    public void BeginPreview()
    {
        if (!CanEdit || Csv.Length == 0) return;
        Busy = true; Preview = null; Confirmed = ConfirmOverrun = false;
    }
    public bool BeginFileRead()
    {
        if (!CanEdit) return false;
        Busy = true; Preview = null; Confirmed = ConfirmOverrun = false;
        return true;
    }
    public void CompleteFileRead(string csv) { if (!Busy || Pending is not null) return; Csv = csv; ClientDirty = false; Busy = false; }
    public void FileReadFailed() { if (Pending is null) Busy = false; }
    public void ShowPreview(EventImportPreview preview) { if (!Busy || Pending is not null) return; Preview = preview; Busy = false; }
    public void PreviewFailed() { if (Pending is null) { Preview = null; Confirmed = ConfirmOverrun = false; } Busy = false; }
    public void Confirm(bool value) { if (CanEdit && Preview is { IsValid: true }) Confirmed = value; }
    public void AcknowledgeOverrun(bool value) { if (CanEdit && NeedsOverrunAcknowledgement) ConfirmOverrun = value; }

    public EventImportPending? BeginOperation(string eventId, string actorId, string requestId)
    {
        if (!CanApply || !Guid.TryParseExact(requestId, "D", out var id) || id.ToString("D") != requestId) return null;
        var p = Preview!;
        Pending = new(eventId, actorId, new(requestId, p.RowVersion, p.Fingerprint, Csv, true, ConfirmOverrun), false);
        Busy = true;
        return Pending;
    }
    public EventImportPending? BeginRetry()
    {
        if (Busy || Pending is null || Accepted is not null) return null;
        Busy = true; return Pending;
    }
    public void SubmissionUnknown()
    {
        if (Pending is not null) Pending = Pending with { HadUnknownResult = true };
        Busy = false;
    }
    public void SubmissionRejected(int status)
    {
        Busy = false;
        if (Pending is null) return;
        if (Pending.HadUnknownResult || status == 403) { if (status == 403) Pending = Pending with { HadUnknownResult = true }; return; }
        Pending = null; Preview = null; Confirmed = ConfirmOverrun = false;
    }
    public bool SubmissionAccepted(EventImportAcceptance acceptance)
    {
        if (Pending is null || Preview is null || acceptance.RequestId != Pending.Request.RequestId
            || acceptance.RowVersion != Pending.Request.RowVersion + 1
            || acceptance.NewSongs != Preview.NewSongs || acceptance.NewTransitions != Preview.NewTransitions
            || acceptance.SkippedRows != Preview.SkippedRows || acceptance.AcceptedAt <= 0)
        { SubmissionUnknown(); return false; }
        Busy = false; Accepted = acceptance; Pending = null; Csv = ""; ClientDirty = false; Confirmed = ConfirmOverrun = false;
        return true;
    }
    public void StartNew()
    {
        if (Busy || Pending is not null || Accepted is null) return;
        Csv = ""; ClientDirty = false; Preview = null; Accepted = null; Confirmed = ConfirmOverrun = false;
    }
    public void Discard()
    {
        if (!CanEdit) return;
        Csv = ""; ClientDirty = false; Preview = null; Confirmed = ConfirmOverrun = false;
    }
}

public sealed record EventImportPending(string EventId, string ActorId, EventImportApply Request, bool HadUnknownResult);
