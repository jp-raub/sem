using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Sem.Web.Components.Shared;

namespace Sem.Web.Components.Pages;

public partial class Participation : IAsyncDisposable
{
    [Inject] private IJSRuntime RefreshJS { get; set; } = default!;
    readonly CancellationTokenSource RefreshStop = new();
    IJSObjectReference? RefreshModule;
    DotNetObjectReference<Participation>? RefreshSink;
    int RefreshLoop;
    bool RefreshDisposed, RefreshBusy, NeedsDraftReview, RefreshFailed;
    long RefreshRevision;
    string RefreshNotice = "";

    protected override void OnInitialized() => Authentication.AuthenticationStateChanged += RefreshAuthenticationChanged;

    async Task InitializeRefresh(bool first)
    {
        if (!first || RefreshDisposed) return;
        try
        {
            var module = await RefreshJS.InvokeAsync<IJSObjectReference>("import", "./participation-refresh.js");
            if (RefreshDisposed) { await module.DisposeAsync(); return; }
            RefreshModule = module;
            RefreshSink = DotNetObjectReference.Create(this);
            RefreshLoop = await module.InvokeAsync<int>("start", RefreshSink);
            if (RefreshDisposed) await module.InvokeVoidAsync("stop", RefreshLoop);
        }
        catch (JSDisconnectedException) { }
    }

    void RefreshAuthenticationChanged(Task<AuthenticationState> state) => _ = InvokeAsync(async () =>
    {
        if (RefreshDisposed) return;
        try
        {
            var principal = (await state).User;
            if (RefreshDisposed) return;
            if (BindContext(principal)) await Load(principal, ++LoadGeneration);
            StateHasChanged();
        }
        catch (OperationCanceledException) { }
    });

    [JSInvokable] public Task RefreshParticipation() => InvokeAsync(RefreshDraft);

    Task RefreshDraft() => RefreshDraftCore(false);
    Task ManualRefreshDraft() => RefreshDraftCore(true);

    async Task RefreshDraftCore(bool manual)
    {
        if (RefreshDisposed || RefreshBusy || Busy || Pending is not null || Uncertain || Plan is null || !InteractiveReady) return;
        RefreshBusy = true;
        var generation = LoadGeneration;
        var eventId = EventId;
        var subject = Subject;
        var binding = BoundIdentity;
        var previous = Plan;
        try
        {
            var principal = (await Authentication.GetAuthenticationStateAsync()).User;
            if (!CanApply()) return;
            if (BindingFor(principal) != binding)
            {
                BindContext(principal);
                await Load(principal, ++LoadGeneration);
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(RefreshStop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var latest = await Core.ReadParticipationAsync(principal, eventId, timeout.Token, subject);
            if (!CanApply()) return;
            var live = (await Authentication.GetAuthenticationStateAsync()).User;
            if (!CanApply()) return;
            if (BindingFor(live) != binding)
            {
                BindContext(live);
                await Load(live, ++LoadGeneration);
                return;
            }
            var update = ParticipationDraftRefresh.Reconcile(previous, latest, Selected, Values, ReleaseMode,
                !string.IsNullOrWhiteSpace(Comment) || !string.IsNullOrWhiteSpace(Reason) || Exception
                || Capacity != (previous.Capacity?.ToString() ?? ""));
            Selected = update.Selected;
            Values = update.Answers;
            if (Capacity == (previous.Capacity?.ToString() ?? "")) Capacity = latest.Capacity?.ToString() ?? "";
            Plan = latest;
            NeedsDraftReview |= update.RequiresReview;
            if (RefreshFailed) RefreshNotice = "通信が回復し、登録状況を更新しました。";
            RefreshFailed = false;
            if (update.RemovedSlots.Length > 0)
                RefreshNotice = "選択した枠が他の登録・変更により選択できなくなったため、その枠だけ選択を解除しました：" + string.Join("、", update.RemovedSlots);
            else if (update.RequiresReview)
                RefreshNotice = "イベント・設問または同じ対象者の保存状態が更新されています。入力は保持しました。変更内容を確認してから続けてください。";
            RefreshRevision++;
            if (manual) Error = "";
        }
        catch (UnauthorizedAccessException)
        {
            if (CanApply()) { ClearContext(); Error = "ログイン状態とイベントの公開・権限を確認してください。"; }
        }
        catch (Exception) when (!RefreshDisposed)
        {
            if (CanApply()) { RefreshFailed = true; RefreshNotice = "最新状態を取得できませんでした。入力は保持しています。通信が戻ったら再取得します。"; }
        }
        catch (OperationCanceledException) when (RefreshDisposed) { }
        finally
        {
            RefreshBusy = false;
            if (!RefreshDisposed) StateHasChanged();
        }
        bool CanApply() => !RefreshDisposed && generation == LoadGeneration && eventId == EventId
            && subject == Subject && binding == BoundIdentity && !Busy && Pending is null && !Uncertain;
    }

    void ConfirmRefreshedDraft()
    {
        if (Busy || RefreshBusy || Pending is not null || Uncertain) return;
        NeedsDraftReview = false;
        RefreshNotice = "更新内容を確認しました。保持した入力で操作を続けられます。";
    }

    void ResetRefreshState() { NeedsDraftReview = false; RefreshFailed = false; RefreshNotice = ""; }

    public async ValueTask DisposeAsync()
    {
        RefreshDisposed = true;
        LoadGeneration++;
        Authentication.AuthenticationStateChanged -= RefreshAuthenticationChanged;
        RefreshStop.Cancel();
        if (RefreshModule is not null)
        {
            try { if (RefreshLoop != 0) await RefreshModule.InvokeVoidAsync("stop", RefreshLoop); await RefreshModule.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        RefreshSink?.Dispose();
        RefreshStop.Dispose();
    }
}
