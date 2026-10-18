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
var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new FakeJs()).BuildServiceProvider();
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
var options=new[]{new AdminCandidateOption("a","Alice <script>","alice@example.test"),new AdminCandidateOption("b","Bob","bob@example.test")};
Check(AdminCandidatePicker.Filter(options,new HashSet<string>{"a"},"",true).Single().Id=="b","Already selected managers excluded from candidates");
Check(AdminCandidatePicker.Filter(options,new HashSet<string>(),"ALICE@",true).Single().Id=="a","Global manager search supports case-insensitive email");
Check(AdminCandidatePicker.Filter(options,new HashSet<string>(),"ALICE@",false).Length==0,"Event-only manager cannot search hidden email");
Check(AdminCandidatePicker.Filter(Enumerable.Range(0,20).Select(i=>new AdminCandidateOption(i.ToString(),"User")).ToArray(),new HashSet<string>(),"",false).Length==10,"Candidate dropdown is bounded to ten options");
var pickerHtml=await renderer.Dispatcher.InvokeAsync(async()=>{
 var rendered=await renderer.RenderComponentAsync<AdminCandidatePicker>(ParameterView.FromDictionary(new Dictionary<string,object?>{["Options"]=options,["SelectedIds"]=new HashSet<string>{"a"},["ShowEmail"]=false}));return rendered.ToHtmlString();});
Check(!pickerHtml.Contains("alice@example.test")&&!pickerHtml.Contains("bob@example.test"),"Name-only picker never renders email into SSR");
Check(!pickerHtml.Contains("<script>")&&pickerHtml.Contains("aria-expanded=\"false\""),"Candidate names are encoded and initial disclosure is closed");
// The same server-side renderer owns both draft preview and saved display.
var html=Sem.Web.Core.EventDescription.Render("<strong>music</strong>\nnext", "text");
Check(html.Contains("&lt;strong&gt;")&&!html.Contains("<strong>music"),"Plain descriptions remain encoded legacy text");
html=Sem.Web.Core.EventDescription.Render("## Music\n**bold** and `code`\n[map](https://venue.example/)","markdown");
Check(html.Contains("<h2>Music</h2>")&&html.Contains("<strong>bold</strong>")&&html.Contains("<code>code</code>"),"Markdown supports headings emphasis and code");
Check(html.Contains("target=\"_blank\"")&&html.Contains("noopener noreferrer"),"Markdown external links have fixed opener and referrer protection");
foreach(var attack in new[]{"<script>alert(1)</script><strong>safe</strong>","<img src=x onerror=alert(1)><p onclick=alert(1)>safe</p>","<svg><a xlink:href='javascript:alert(1)'>bad</a></svg><p>safe</p>","<form action='https://evil.example'><input name=password></form><p>safe</p>","<iframe srcdoc='<script>alert(1)</script>'></iframe><p>safe</p>","<math><mtext><table><mglyph><style><!--</style><img title='--><img src=x onerror=alert(1)>'></math><p>safe</p>"}) {
    html=Sem.Web.Core.EventDescription.Render(attack,"html");var document=new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
    Check(!document.QuerySelectorAll("script,img,svg,math,form,input,iframe,style,object,embed").Any()&&!document.All.SelectMany(e=>e.Attributes).Any(a=>a.Name.StartsWith("on")||a.Name=="style"),"HTML sanitizer rejects active elements and attributes: "+attack.Split('>')[0]);
}
html=Sem.Web.Core.EventDescription.Render("<a href='javascript:alert(1)'>script</a><a href='//evil.example/'>relative</a><a href='https://user:password@evil.example/'>credential</a><a href='https://venue.example/' style='color:red' id='spoof' target='_self'>map</a>","html");
var links=new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html).QuerySelectorAll("a");
Check(links.Length==4&&links.Take(3).All(e=>!e.HasAttribute("href"))&&links[3].GetAttribute("href")=="https://venue.example/","Only absolute credential-free HTTP(S) links survive HTML sanitization");
Check(links.All(e=>!e.HasAttribute("style")&&!e.HasAttribute("id"))&&links[3].GetAttribute("rel")=="noopener noreferrer","HTML attributes cannot spoof layout IDs or navigation policy");
html=Sem.Web.Core.EventDescription.Render("<strong>untrusted</strong>","unknown");
Check(!html.Contains("<strong>"),"Unknown persisted description format fails closed to text");
var renderedDescription=await renderer.Dispatcher.InvokeAsync(async()=>{var result=await renderer.RenderComponentAsync<EventDescriptionContent>(ParameterView.FromDictionary(new Dictionary<string,object?>{["Source"]="<b>music</b><script>bad()</script>",["Format"]="html"}));return result.ToHtmlString();});
Check(renderedDescription.Contains("<b>music</b>")&&new AngleSharp.Html.Parser.HtmlParser().ParseDocument(renderedDescription).QuerySelectorAll("script").Length==0&&!renderedDescription.Contains("bad()"),"Real preview component uses sanitized markup without active content");
foreach(var format in new[]{"markdown","html"})
{
 var listHtml=await renderer.Dispatcher.InvokeAsync(async()=>{
  var result=await renderer.RenderComponentAsync<EventListDescription>(ParameterView.FromDictionary(new Dictionary<string,object?>{["Source"]="<b>raw</b> **raw**",["Format"]=format}));return result.ToHtmlString();});
 Check(string.IsNullOrWhiteSpace(listHtml), "Event list omits rich source entirely: "+format);
}
var textList=await renderer.Dispatcher.InvokeAsync(async()=>{
 var result=await renderer.RenderComponentAsync<EventListDescription>(ParameterView.FromDictionary(new Dictionary<string,object?>{["Source"]="<b>legacy text</b>"}));return result.ToHtmlString();});
Check(textList.Contains("workspace-event-description")&&textList.Contains("&lt;b&gt;legacy text&lt;/b&gt;")&&!textList.Contains("<b>"),"Legacy text list description stays visible and encoded");
var helpHtml=await renderer.Dispatcher.InvokeAsync(async()=>{
 var result=await renderer.RenderComponentAsync<FieldHelp>(ParameterView.FromDictionary(new Dictionary<string,object?>{["HelpKey"]="event.title",["Label"]="Event"}));return result.ToHtmlString();});
var helpElement=new AngleSharp.Html.Parser.HtmlParser().ParseDocument(helpHtml).QuerySelector("[data-field-help]")!;
Check(helpElement.TagName=="SPAN"&&helpElement.GetAttribute("role")=="button"&&helpElement.GetAttribute("tabindex")=="-1","Real help component is non-labelable and excluded from sequential form focus");
Check(helpElement.GetAttribute("aria-keyshortcuts")=="Alt+F1"&&helpElement.GetAttribute("title")!.Contains("Alt+F1"),"Real help component documents the alternative keyboard action");
Console.WriteLine($"UI feedback, sort and admin picker checks passed: {checks}");

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
