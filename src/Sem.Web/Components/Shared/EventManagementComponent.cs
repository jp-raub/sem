using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Sem.Web.Runtime;

namespace Sem.Web.Components.Shared;

// UI lifetime boundary only. Core services remain the authorization authority.
public abstract class EventManagementComponent : ComponentBase, IDisposable
{
    [Parameter] public string EventId { get; set; } = "";
    [Parameter] public EventCallback<string> AccessDenied { get; set; }
    [Parameter] public EventCallback<string> Saved { get; set; }
    [Parameter] public bool Embedded { get; set; }
    [Parameter] public long ContextGeneration { get; set; }
    [Parameter] public string ContextIdentityKey { get; set; } = "";
    [Inject] protected AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] protected IOptions<IdentityOptions> IdentityOptions { get; set; } = default!;
    protected sealed record Identity(string Actor, string Stamp, string Session);
    protected sealed record Ticket(long Revision, string EventId, long Generation, string ContextKey, Identity Binding, ClaimsPrincipal Principal);
    Identity? BoundIdentity;
    string? BoundEvent;
    long BoundGeneration, Revision;
    string BoundKey = "";
    bool Bound, Disposed;
    protected override void OnInitialized() => Authentication.AuthenticationStateChanged += AuthenticationChanged;
    protected override async Task OnParametersSetAsync() => await Reconcile((await Authentication.GetAuthenticationStateAsync()).User);
    Identity? Binding(ClaimsPrincipal p)
    {
        var claims = IdentityOptions.Value.ClaimsIdentity;
        var actor = p.FindFirst(claims.UserIdClaimType)?.Value;
        var stamp = p.FindFirst(claims.SecurityStampClaimType)?.Value;
        var session = p.FindFirst(PasskeyAdditionService.SessionClaim)?.Value;
        return p.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(actor) && !string.IsNullOrWhiteSpace(stamp) && !string.IsNullOrWhiteSpace(session)
            ? new(actor, SemEndpoints.Hash(stamp), SemEndpoints.Hash(session)) : null;
    }
    static string Key(Identity? identity) => identity is null ? "" : SemEndpoints.Hash($"{identity.Actor}\u001f{identity.Stamp}\u001f{identity.Session}");
    async Task Reconcile(ClaimsPrincipal principal)
    {
        if (Disposed) return;
        var identity = Binding(principal);
        if (Bound && BoundEvent == EventId && BoundIdentity == identity && BoundGeneration == ContextGeneration && BoundKey == ContextIdentityKey) return;
        Bound = true; BoundEvent = EventId; BoundIdentity = identity; BoundGeneration = ContextGeneration; BoundKey = ContextIdentityKey; Revision++;
        ClearPrivateState();
        if (identity is not null && (!Embedded || ContextIdentityKey == Key(identity))) await LoadCurrentAsync();
    }
    void AuthenticationChanged(Task<AuthenticationState> state) => _ = InvokeAsync(async () => { if (Disposed) return; await Reconcile((await state).User); if (!Disposed) StateHasChanged(); });
    protected async Task<Ticket?> BeginOperation()
    {
        var principal = (await Authentication.GetAuthenticationStateAsync()).User;
        if (Disposed) return null;
        if (Binding(principal) != BoundIdentity || BoundEvent != EventId || BoundGeneration != ContextGeneration || BoundKey != ContextIdentityKey)
        { await Reconcile(principal); return null; }
        if (BoundIdentity is null || Embedded && ContextIdentityKey != Key(BoundIdentity)) return null;
        return new(++Revision, EventId, ContextGeneration, ContextIdentityKey, BoundIdentity, principal);
    }
    protected bool SameContext(Ticket t) => !Disposed && EventId == t.EventId && BoundEvent == t.EventId && ContextGeneration == t.Generation && ContextIdentityKey == t.ContextKey && BoundIdentity == t.Binding;
    protected bool Current(Ticket t) => SameContext(t) && Revision == t.Revision;
    protected Task NotifyAccessDenied(Ticket ticket) => SameContext(ticket) && AccessDenied.HasDelegate ? AccessDenied.InvokeAsync(ticket.EventId) : Task.CompletedTask;
    protected Task NotifySaved(Ticket ticket) => SameContext(ticket) && Saved.HasDelegate ? Saved.InvokeAsync(ticket.EventId) : Task.CompletedTask;
    protected abstract void ClearPrivateState();
    protected abstract Task LoadCurrentAsync();
    public void Dispose() { if (Disposed) return; Disposed = true; Revision++; Authentication.AuthenticationStateChanged -= AuthenticationChanged; }
}
