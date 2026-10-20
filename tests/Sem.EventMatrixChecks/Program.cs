using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sem.Web.Components.Shared;
using Sem.Web.Core;

var services = new ServiceCollection().AddLogging().BuildServiceProvider();
await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
var start = new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.FromHours(9));
TimedPart Row(string id, string kind, string? songId, string title) =>
    new(id, kind, title, "", "🎵", songId, null, start, start.AddMinutes(5), 300, null);
ParticipationSlot Slot(string id, string songId, string part, string? owner = null, string? name = null) =>
    new(id, songId, "曲 " + songId, part, "Instrument", owner, name, null, 1);
async Task<string> Render(IReadOnlyList<TimedPart> rows, ParticipationSlot[] slots,
    IReadOnlySet<string>? selected = null, bool release = false, bool disabled = false, bool decode = true, bool profiles = false)
{
    return await renderer.Dispatcher.InvokeAsync(async () =>
    {
        var output = await renderer.RenderComponentAsync<EventPartMatrix>(ParameterView.FromDictionary(
            new Dictionary<string, object?>
            {
                ["Rows"] = rows,
                ["OnViewProfile"] = profiles ? EventCallback.Factory.Create<string>(new object(), (string _) => {}) : default(EventCallback<string>),
                ["Slots"] = slots,
                ["SelectedIds"] = selected ?? new HashSet<string>(),
                ["OwnUserId"] = "me",
                ["ReleaseMode"] = release,
                ["IsDisabled"] = (Func<ParticipationSlot, bool>)(_ => disabled)
            }));
        var raw = output.ToHtmlString();
        return decode ? WebUtility.HtmlDecode(raw) : raw;
    });
}

var transition = Row("opening", "Transition", null, "開場案内");
var html = await Render([transition], []);
Check(html.Contains("開場案内") && html.Contains("<table"), "Progress rows render with zero part columns");
Check(!html.Contains("colspan=\"0\""), "Zero-column progress never emits invalid colspan");
Check(!html.Contains("現在、表示できる曲の参加枠はありません"), "Progress-only event is not reported as empty");

html = await Render([], []);
Check(html.Contains("現在、表示できる曲の参加枠はありません") && !html.Contains("<table"), "Truly empty matrix has an explicit empty state");
html = await Render([], [Slot("unplaced", "s1", "Guitar")]);
Check(html.Contains("参加枠はありますが") && !html.Contains("現在、表示できる曲の参加枠はありません"), "Unplaced slots are distinguished from no slots");

var song = Row("song-row", "Song", "s1", "合奏 <song>") with { Artist = "Artist <name> & Co" };
var slots = new[]
{
    Slot("free", "s1", "Guitar"),
    Slot("own", "s1", "Guitar", "me", "本人 <Alice>"),
    Slot("other", "s1", "Drums", "other-user", "相手 & Bob")
};
html = await Render([song, transition], slots, new HashSet<string> { "free" });
if (Environment.GetEnvironmentVariable("SEM_MATRIX_RENDER_OUTPUT_DIR") is { Length: > 0 } outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "matrix-synthetic.html"),
        await Render([song, transition], slots, new HashSet<string> { "free" }, decode: false));
}
Check(Regex.Matches(html, "data-event-matrix-slot=").Count == 3, "Every displayed slot has exactly one button");
Check(Regex.Matches(html, "<th\\b(?=[^>]*\\bscope=\"col\")[^>]*>Guitar</th>").Count == 1, "Duplicate part definitions form one column");
Check(html.IndexOf(">Guitar</th>", StringComparison.Ordinal) < html.IndexOf(">Drums</th>", StringComparison.Ordinal), "Part order follows the existing slot order");
Check(html.Contains("aria-pressed=\"true\""), "Selected state is exposed to assistive technology");
Check(Regex.Matches(html, "aria-pressed=\"false\"").Count == 2, "Unselected buttons explicitly expose a false pressed state");
var rawSlotsHtml = await Render([song, transition], slots, new HashSet<string> { "free" }, decode: false);
Check(Regex.IsMatch(html, "<small\\b[^>]*class=\"event-matrix-artist\"[^>]*>Artist <name> & Co</small>"),
    "Artist has a dedicated alignment class independent of preparation notes");
Check(rawSlotsHtml.Contains("Artist &lt;name&gt; &amp; Co") && !rawSlotsHtml.Contains("Artist <name>"),
    "Artist alignment preserves HTML encoding");
Check(Regex.IsMatch(html, "<th\\b[^>]*scope=\"col\"[^>]*class=\"event-matrix-duration\"[^>]*>所要時間</th>"),
    "Duration header shares its right-alignment class with duration cells");
string SlotButton(string markup, string id) => Regex.Match(markup,
    "<button\\b[^>]*data-event-matrix-slot=\"" + Regex.Escape(id) + "\"[^>]*>.*?</button>",
    RegexOptions.Singleline).Value;
string ButtonText(string markup) => System.Net.WebUtility.HtmlDecode(Regex.Replace(
    Regex.Replace(markup, "<small\\b[^>]*>.*?</small>", "", RegexOptions.Singleline),
    "^<button\\b[^>]*>|</button>$", "", RegexOptions.Singleline)).Trim();
Check(ButtonText(SlotButton(rawSlotsHtml, "own")) == "本人 <Alice>" && ButtonText(SlotButton(rawSlotsHtml, "other")) == "相手 & Bob",
    "Assigned button text is the exact assigned name without ownership prefixes");
Check(html.Contains("aria-label=\"合奏 <song> / Guitar 2 / 2 / 本人 <Alice>\""), "Accessible name retains the assigned name without a self prefix");
Check(html.Contains("aria-label=\"合奏 <song> / Drums / 相手 & Bob\""), "Accessible name retains the assigned name without an other prefix");
Check(SlotButton(rawSlotsHtml, "own").Contains("event-matrix-own") && SlotButton(rawSlotsHtml, "other").Contains("event-matrix-occupied"),
    "Own and other slots retain their distinct visual classes");
Check(SlotButton(html, "own").Contains("aria-description=\"ログイン中のアカウントの登録枠\"")
    && SlotButton(html, "other").Contains("aria-description=\"他の参加者の登録枠\"")
    && !SlotButton(rawSlotsHtml, "free").Contains("aria-description"),
    "Ownership context is a separate accessible description, absent for empty slots");
Check(html.Contains("colspan=\"2\""), "Progress spans only existing part columns");
Check(html.Contains("tabindex=\"0\"") && html.Contains("横スクロールできます"), "Scrollable region is keyboard reachable and named");
Check(!html.Contains("id=\""), "Matrix does not introduce global DOM ids");

var unnamedHtml = await Render([song], [Slot("unnamed", "s1", "Guitar", "me")], decode: false);
Check(ButtonText(SlotButton(unnamedHtml, "unnamed")) == "登録済み", "Missing public name retains the existing neutral fallback without an ownership prefix");

html = await Render([song], slots, new HashSet<string> { "own" }, release: true, disabled: true);
Check(ButtonText(SlotButton(await Render([song], slots, new HashSet<string> { "own" }, release: true, disabled: true, decode: false), "own")) == "解除選択中・本人 <Alice>", "Release selection preserves the exact name and selection status without an ownership prefix");
Check(html.Contains("本人 <Alice> / 解除選択中"), "Release selection preserves the accessible name and status");
var disabledHtml = await Render([song], slots, new HashSet<string> { "own" }, release: true, disabled: true, decode: false);
Check(Regex.Matches(disabledHtml, "<button[^>]*\\sdisabled(?=\\s|>)", RegexOptions.Singleline).Count == 3, "Parent-supplied disabling is honored for every slot");
html = await renderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await renderer.RenderComponentAsync<EventPartMatrix>(ParameterView.FromDictionary(
        new Dictionary<string, object?>
        {
            ["Rows"] = new[] { song },
            ["Slots"] = slots,
            ["SelectedIds"] = new HashSet<string>(),
            ["IsDisabled"] = (Func<ParticipationSlot, bool>)(_ => false)
        }));
    return output.ToHtmlString();
});
Check(!html.Contains("<Alice>") && !html.Contains("<song>"), "Assigned names and song names are HTML encoded");
Check(TimetableDisplay.Clock(start,"seconds",start)=="10:00:00", "Same-day display omits repeated date but retains seconds");
Check(TimetableDisplay.Clock(start,"minutes",start)=="10:00", "Configured minute display retains minute precision");
Check(TimetableDisplay.Clock(start.AddDays(1),"seconds",start)=="2026/10/18 10:00:00", "Day crossing keeps explicit full date");
Check(TimetableDisplay.Clock(start.AddDays(-1),"seconds",start)=="2026/10/16 10:00:00", "Prior-day opening keeps explicit date");
Check(start.Second==0 && TimetableCalculator.Clock(start,"seconds")=="2026/10/17 10:00:00", "Existing calculation/editor date format is unchanged");
var profileHtml = await Render([song], slots, disabled:true, profiles:true);
Check(profileHtml.Contains("data-event-profile=\"me\"") && profileHtml.Contains("data-event-profile=\"other-user\""), "Assigned matrix slots expose profile actions for self and others");
Check(Regex.Matches(profileHtml, "disabled").Count == 1, "Profile actions remain available when registration is disabled, while the empty slot stays disabled");
var releaseHtml = await Render([song], slots, release:true, disabled:true, profiles:true);
Check(!releaseHtml.Contains("data-event-profile"), "Release mode preserves registration controls instead of opening profiles");
// Inspect the compiled dialog frames, not only its Razor source. Browsers that
// validate setAttribute names reject an accidentally literal Razor directive.
#pragma warning disable BL0006 // Test-only inspection of compiled RenderTree frames.
string[] CompiledAttributes(ComponentBase component)
{
    using var tree = new Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder();
    component.GetType().GetMethod("BuildRenderTree", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(component, [tree]);
    var frames = tree.GetFrames();
    return frames.Array.Take(frames.Count)
        .Where(frame => frame.FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute)
        .Select(frame => frame.AttributeName).ToArray();
}
var cardAttributes = CompiledAttributes(new ProfileCard());
Check(!cardAttributes.Any(name => name.StartsWith("@", StringComparison.Ordinal)),
    "Compiled profile dialog emits no literal Razor directives as HTML attributes");
Check(cardAttributes.Contains("data-dialog-close"), "Profile dialog exposes its guarded close action to native cancel");
Check(cardAttributes.Contains("onclick"), "Profile dialog close remains a real Blazor click handler");
var comments = new Sem.Web.Components.Pages.Comments();
typeof(Sem.Web.Components.Pages.Comments).GetProperty("Embedded")!.SetValue(comments, true);
typeof(Sem.Web.Components.Pages.Comments).GetField("Plan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
    .SetValue(comments, new CommentPlan("event", "Event", 1, false, true, null, [], null));
var commentAttributes = CompiledAttributes(comments);
Check(!commentAttributes.Any(name => name.StartsWith("@", StringComparison.Ordinal)),
    "Compiled embedded comment dialog emits no literal Razor directives as HTML attributes");
Check(commentAttributes.Contains("data-dialog-close"), "Comment dialog exposes its guarded close action to native cancel");
Check(commentAttributes.Contains("onclick"), "Comment dialog close remains a real Blazor click handler");
#pragma warning restore BL0006
await EntrySummaryChecks.Run(renderer, Check);
Console.WriteLine($"PASS {checks} matrix and entry summary rendering checks (not interactive browser acceptance)");
