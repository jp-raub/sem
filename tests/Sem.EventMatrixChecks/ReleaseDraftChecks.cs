using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Sem.Web.Components.Pages;
using Sem.Web.Core;

static class ReleaseDraftChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var page = new Participation();
        var type = page.GetType();
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(page, value);
        T Get<T>(string name) => (T)type.GetField(name, flags)!.GetValue(page)!;
        Task Submit() => (Task)type.GetMethod("Submit", flags)!.Invoke(page, null)!;
        var authentication = new HeldAuthentication();
        type.GetProperty("Authentication", flags)!.SetValue(page, authentication);
        var plan = new ParticipationPlan("event", "Event", 4, "phase", "Custom", "Phase", 2, 1,
            null, 1, true, "subject", false,
            [new("slot", "song", "Song", "Voice", "Sing", "subject", "Subject", "phase", 7)], [], [], 9);
        Set("Plan", plan); Set("CurrentId", "manager"); Set("Subject", "subject");
        Set("Selected", new HashSet<string> { "slot" }); Set("_release", true);
        Set("Reason", "Explicit release reason");
        var running = Submit();
        var pending = Get<ParticipationEdit>("Pending");
        check(!Get<bool>("_release") && Get<string>("Reason") == "" && Get<HashSet<string>>("Selected").Count == 0,
            "Release click immediately resets visible mode, reason and selections before authentication completes");
        check(pending.Kind == "Release" && pending.Reason == "Explicit release reason"
            && pending.SubjectId == "subject" && pending.Slots.Single() == new SlotChoice("slot", 7)
            && pending.ParticipantVersion == 9 && pending.RowVersion == 4,
            "Pending release snapshot retains original reason, subject and concurrency versions");
        check(Get<bool>("Busy") && !running.IsCompleted, "Release remains busy while its captured request is in flight");
        await Submit();
        check(ReferenceEquals(pending, Get<ParticipationEdit>("Pending")),
            "Busy double click cannot replace or duplicate the captured release operation");

        // Fault injection only: no Core service is supplied to this isolated
        // state test, so dispatch enters the existing unknown-result boundary.
        authentication.Complete();
        await running;
        check(Get<bool>("Uncertain") && !Get<bool>("Busy")
            && ReferenceEquals(pending, Get<ParticipationEdit>("Pending")),
            "Unknown release result preserves its original operation for explicit retry");
        await Submit();
        check(Get<bool>("Uncertain") && ReferenceEquals(pending, Get<ParticipationEdit>("Pending"))
            && pending.Reason == "Explicit release reason" && pending.Kind == "Release",
            "Retry after visible reset still uses the same release ID and original reason, never an answer/register request");
        check(!Get<bool>("_release") && Get<string>("Reason") == "",
            "Unknown result and explicit retry do not restore stale visible release mode or reason");
    }
    sealed class HeldAuthentication : AuthenticationStateProvider
    {
        readonly TaskCompletionSource<AuthenticationState> source = new();
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => source.Task;
        public void Complete() => source.SetResult(new(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}