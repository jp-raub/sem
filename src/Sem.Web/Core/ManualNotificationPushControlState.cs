namespace Sem.Web.Core;

public sealed record ManualPushControlContext(string UserId, bool Enabled, long Epoch, bool DeliveryConnected, bool PushConfigured);
public sealed record ManualPushRuntime(bool DeliveryConnected);
// Circuit memory only. Current context and historical operation result are deliberately distinct.
public sealed class ManualPushControlState
{
    public string ActorId { get; private set; } = "";
    public ManualPushControlContext? View { get; private set; }
    public bool? Target { get; private set; }
    public bool Confirmed { get; private set; }
    public bool Busy { get; private set; }
    public bool Uncertain { get; private set; }
    public ManualNotificationPushActivationEdit? Pending { get; private set; }
    public ManualNotificationPushActivationResult? Result { get; private set; }
    public bool CanEdit => View is not null && !Busy && Pending is null;
    public bool NavigationBlocked => Busy || Pending is not null || Target is not null;
    public void Context(ManualPushControlContext view)
    {
        if (string.IsNullOrEmpty(view.UserId) || view.Epoch < 0 || ActorId.Length > 0 && ActorId != view.UserId) throw new UnauthorizedAccessException();
        if (Pending is null) { Target = null; Confirmed = false; } // explicit refresh invalidates unsent approval
        ActorId = view.UserId; View = view;
    }
    public void Denied() { View = null; }
    public void Stage(bool enabled)
    {
        if (!CanEdit) throw new InvalidOperationException();
        Target = enabled; Confirmed = false; Result = null;
    }
    public void Confirm(bool value) { if (!CanEdit || Target is null) throw new InvalidOperationException(); Confirmed = value; }
    public void Discard() { if (Busy || Pending is not null) throw new InvalidOperationException(); Target = null; Confirmed = false; }
    public ManualNotificationPushActivationEdit Begin()
    {
        if (Busy || View is null) throw new InvalidOperationException();
        if (Pending is null) {
            if (Target is null || !Confirmed) throw new InvalidOperationException();
            Pending = new(Guid.NewGuid().ToString("D"), Target.Value, View.Epoch, true);
        }
        Busy = true; return Pending;
    }
    public void Unknown() { if (Pending is null) throw new InvalidOperationException(); Busy = false; Uncertain = true; }
    public void Rejected()
    {
        if (Pending is null) throw new InvalidOperationException(); Busy = false;
        if (Uncertain) return; // a rejection now cannot disprove an earlier committed attempt
        Pending = null; Target = null; Confirmed = false; View = null; // require a fresh current-state read after rejection
    }
    public void Accepted(ManualNotificationPushActivationResult result)
    {
        if (Pending is null || result.RequestId != Pending.RequestId || result.Enabled != Pending.Enabled ||
            result.Epoch < 0 || result.Changed && Pending.ExpectedEpoch == long.MaxValue ||
            result.Epoch != Pending.ExpectedEpoch + (result.Changed ? 1 : 0)) throw new InvalidOperationException("Mismatched control result");
        Result = result; Pending = null; Target = null; Confirmed = Busy = Uncertain = false;
        View = null; // historical replay MUST NOT overwrite the current ON/OFF; independently fetch current state
    }
}
