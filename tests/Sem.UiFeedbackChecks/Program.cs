using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using Microsoft.JSInterop;
using Sem.Web.Components.Shared;

var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var component = new OperationFeedback();
var js = new FakeJs();
var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
void Set(string name, object value) => typeof(OperationFeedback).GetProperty(name, flags)!.SetValue(component, value);
Task Render() => (Task)typeof(OperationFeedback).GetMethod("OnAfterRenderAsync", flags)!.Invoke(component, [false])!;
Set("JS", js);
Set("Success", "old status"); await Render();
Check(js.Module.Calls.Count == 0, "Initial read and revision zero never open notification");
Set("Revision", 1L); Set("Busy", true); await Render();
Check(js.Module.Calls.Count == 0, "Busy save does not announce old result");
Set("Success", "保存しました。"); Set("Busy", false); await Render();
Check(js.Module.Calls.Single().Kind == "success", "Completed save emits success once");
await Render(); Set("Success", "reread status"); await Render();
Check(js.Module.Calls.Count == 1, "Rerenders and refresh cannot replay operation feedback");
Set("Revision", 2L); Set("Success", "保存しました。"); await Render();
Check(js.Module.Calls.Count == 2, "A separate save with identical text still notifies");
Set("Revision", 3L); Set("Error", "stale version"); await Render();
Check(js.Module.Calls.Last() == ("error", "stale version"), "Error takes precedence over stale success message");
Set("Revision", 4L); Set("Unknown", true); await Render();
Check(js.Module.Calls.Last().Kind == "unknown", "Response loss is not a definite failure");
Set("Revision", 5L); Set("Unknown", false); Set("Error", ""); Set("ResultKind", "partial"); Set("Success", "2 success, 1 occupied"); await Render();
Check(js.Module.Calls.Last().Kind == "partial", "Partial success does not become all-success toast");
Set("Revision", 6L); Set("Success", ""); await Render();
Check(js.Module.Calls.Count == 5, "Empty pending result does not consume revision");
Set("Success", "settled result"); await Render();
Check(js.Module.Calls.Count == 6, "Result arriving later for same revision is delivered");
await component.DisposeAsync();
Check(js.Module.Releases == 1 && js.Module.Disposals == 1, "Component disposal releases only owned notification and module");
Set("Revision", 7L); await Render();
Check(js.Module.Calls.Count == 6, "Disposed component never emits stale completion");
Check(js.Identifiers.All(x => x == "import"), "Component uses presentation module, no mutation interop");
// The displayed order is a copy/view; underlying manual order and cursor inputs stay intact.
var rows = new[] {
    new SortRow("b", "い", "2026-10-18T01:00:00+09:00"),
    new SortRow("c", "あ", "2026-10-17T17:00:00+00:00"),
    new SortRow("a", "あ", "2026-10-17T17:00:00+00:00") };
Check(string.Join(",", DisplayOrdering.Names(rows, x=>x.Name, x=>x.Id).Select(x=>x.Id)) == "a,c,b", "Japanese name ascending with deterministic ID ties");
Check(DisplayOrdering.Names(rows,x=>x.Name,x=>x.Id,true).First().Id == "b", "Name descending changes presentation only");
Check(string.Join(",",DisplayOrdering.Dates(rows,x=>x.Date,x=>x.Id).Select(x=>x.Id)) == "a,c,b", "Default dates descend by actual instant, not date string timezone");
Check(DisplayOrdering.Dates(rows,x=>x.Date,x=>x.Id,false).First().Id == "b", "Date ascending reverses instants without changing equal-date identity order");
Check(string.Join(",",rows.Select(x=>x.Id)) == "b,c,a", "Display sorting never mutates original manual order");
Check(!DisplayOrdering.Names(Array.Empty<SortRow>(),x=>x.Name,x=>x.Id).Any(), "Empty lists retain empty state");
var services = new ServiceCollection().AddLogging().BuildServiceProvider();
await using var renderer = new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
async Task<string> Heading(bool active,bool descending,bool disabled=false) => await renderer.Dispatcher.InvokeAsync(async()=> {
    var rendered = await renderer.RenderComponentAsync<SortHeading>(ParameterView.FromDictionary(new Dictionary<string,object?>{["Label"]="<Name>",["Active"]=active,["Descending"]=descending,["Disabled"]=disabled}));return rendered.ToHtmlString();
});
var heading = await Heading(false,false);
Check(heading.Contains("aria-sort=\"none\"") && System.Net.WebUtility.HtmlDecode(heading).Contains("↕"), "Inactive column exposes none and visible bidirectional arrow");
heading=await Heading(true,false);
Check(heading.Contains("aria-sort=\"ascending\"") && System.Net.WebUtility.HtmlDecode(heading).Contains("▲"), "Ascending state matches visible arrow and ARIA");
heading=await Heading(true,true,true);
Check(heading.Contains("aria-sort=\"descending\"") && System.Net.WebUtility.HtmlDecode(heading).Contains("▼") && heading.Contains("disabled"), "Descending disabled control has consistent semantic state");
Check(heading.Contains("type=\"button\"") && !heading.Contains("<Name>"), "Sort buttons never submit forms and labels are encoded");
var clicks=0;var sortHeading=new SortHeading();
typeof(SortHeading).GetProperty("Changed")!.SetValue(sortHeading, EventCallback.Factory.Create(new object(),()=>clicks++));
await sortHeading.Changed.InvokeAsync();Check(clicks==1,"Sort callback is explicit and invokes once");
Console.WriteLine($"UI feedback and display sorting checks passed: {checks}");

sealed class FakeJs : IJSRuntime
{
    public FakeModule Module { get; } = new();
    public List<string> Identifiers { get; } = [];
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    { Identifiers.Add(identifier); return ValueTask.FromResult((TValue)(object)Module); }
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<TValue>(identifier, args);
}
sealed class FakeModule : IJSObjectReference
{
    public List<(string Kind, string Text)> Calls { get; } = [];
    public int Releases, Disposals;
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        if (identifier == "notify") Calls.Add(((string)args![1]!, (string)args[2]!));
        else if (identifier == "release") Releases++;
        else throw new InvalidOperationException(identifier);
        return ValueTask.FromResult(default(TValue)!);
    }
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}

sealed record SortRow(string Id,string Name,string Date);
