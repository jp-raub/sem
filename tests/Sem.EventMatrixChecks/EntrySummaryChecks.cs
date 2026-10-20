using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Sem.Web.Components.Shared;
using Sem.Web.Core;

static class EntrySummaryChecks
{
    public static async Task Run(HtmlRenderer renderer, Action<bool, string> check)
    {
        ParticipationSlot Slot(string id, string song, string? owner, string? phase = "phase") =>
            new(id, song, "曲", "パート", "Play", owner, owner, phase, 1);
        var plan = new ParticipationPlan("event", "Event", 1, "phase", "Custom", "受付 <phase> & Co",
            2, 1, null, 1, true, "subject", false,
            [Slot("voice", "song-a", "subject"), Slot("guitar", "song-a", "subject"), Slot("free", "song-b", null)],
            [], [], 1);
        async Task<string> Render(ParticipationPlan value, bool self = true) =>
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<ParticipationEntrySummary>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    { ["Plan"] = value, ["IsSelf"] = self }));
                return output.ToHtmlString();
            });
        string Meter(string html) => Regex.Match(html, "<div\\b[^>]*role=\"meter\"[^>]*>").Value;
        var html = await Render(plan);
        check(WebUtility.HtmlDecode(html).Contains("登録済み 1 曲 / 上限 2 曲"),
            "Summary uses authoritative distinct-song count, not two assigned parts");
        check(Meter(html).Contains("aria-valuenow=\"1\"") && Meter(html).Contains("aria-valuemax=\"2\"")
            && html.Contains("width:50%"), "Finite summary exposes a valid named numeric meter");
        check(WebUtility.HtmlDecode(html).Contains("受付 <phase> & Co") && html.Contains("&lt;phase&gt; &amp; Co") && !html.Contains("<phase>"),
            "Summary phase names retain HTML encoding");
        check(WebUtility.HtmlDecode(html).Contains("自分の参加登録"), "Self summary identifies its subject");
        check(!html.Contains("id=\"") && !html.Contains("<button") && !html.Contains("<input"),
            "Read-only summary adds no global ids, actions or focus stops");
        check(!html.Contains("aria-live"), "Polling summary does not repeatedly announce unchanged content");
        check(WebUtility.HtmlDecode(html).Contains("未送信の選択は含みません"),
            "Finite summary explicitly excludes unsubmitted selections");
        html = await Render(plan, false);
        check(WebUtility.HtmlDecode(html).Contains("管理者が選択中の対象者の参加登録")
            && !WebUtility.HtmlDecode(html).Contains("自分の参加登録"),
            "Admin subject summary does not mislabel another person's count as self");
        html = await Render(plan with { OwnPhaseSongCount = 0 });
        check(Meter(html).Contains("aria-valuenow=\"0\"") && html.Contains("width:0%"),
            "Zero saved songs remain zero despite available slots");
        html = await Render(plan with { OwnPhaseSongCount = 2 });
        check(Meter(html).Contains("aria-valuenow=\"2\"") && html.Contains("width:100%")
            && WebUtility.HtmlDecode(html).Contains("曲数上限に達しています"), "Reached limit is explicit");
        html = await Render(plan with { OwnPhaseSongCount = 3 });
        check(Meter(html).Contains("aria-valuenow=\"2\"") && html.Contains("width:100%")
            && WebUtility.HtmlDecode(html).Contains("登録済み 3 曲 / 上限 2 曲")
            && WebUtility.HtmlDecode(html).Contains("既存登録は自動解除されません"),
            "Over-limit actual text is preserved while meter range stays valid");
        html = await Render(plan with { MaxPicks = int.MaxValue, OwnPhaseSongCount = int.MaxValue });
        check(html.Contains("width:100%") && !html.Contains("width:-"),
            "Large valid limits do not overflow percentage arithmetic");
        html = await Render(plan with { MaxPicks = 0, OwnPhaseSongCount = 0 });
        check(!html.Contains("role=\"meter\"") && html.Contains("aria-hidden=\"true\"")
            && WebUtility.HtmlDecode(html).Contains("パート登録の上限は0曲")
            && !WebUtility.HtmlDecode(html).Contains("パート登録受付中"),
            "Zero limit has a nonsemantic empty track and does not claim registration is open");
        html = await Render(plan with { MaxPicks = 0, OwnPhaseSongCount = 1 });
        check(WebUtility.HtmlDecode(html).Contains(" / 0 曲") && !html.Contains("role=\"meter\""),
            "Existing registrations with a zero limit never produce an invalid ARIA maximum");
        html = await Render(plan with { MaxPicks = -1 });
        check(WebUtility.HtmlDecode(html).Contains("空き枠数 / 全体枠数（イベント全体）")
            && Meter(html).Contains("aria-valuenow=\"1\"") && Meter(html).Contains("aria-valuemax=\"3\""),
            "Unlimited summary uses event-wide free slots instead of subject songs");
        check(WebUtility.HtmlDecode(html).Contains("選択可能数ではありません"),
            "Free-slot summary does not promise eligibility or bypass capacity");
        html = await Render(plan with { MaxPicks = -1, Slots = [] });
        check(!html.Contains("role=\"meter\"") && WebUtility.HtmlDecode(html).Contains(" / 0 枠")
            && !html.Contains("NaN") && !html.Contains("Infinity"),
            "Unlimited 0/0 has no zero division or invalid numeric meter");
        html = await Render(plan with { MaxPicks = -1, Slots = [Slot("used", "song-a", "other")] });
        check(Meter(html).Contains("aria-valuenow=\"0\"") && Meter(html).Contains("aria-valuemax=\"1\""),
            "Fully occupied unlimited event reports zero free slots");
        html = await Render(plan with { MaxPicks = -1, Slots = [Slot("new", "song-c", null), .. plan.Slots] });
        check(Meter(html).Contains("aria-valuenow=\"2\"") && Meter(html).Contains("aria-valuemax=\"4\""),
            "Updated slot collection changes unlimited free and total counts");
        foreach (var kind in new[] { "Hidden", "View", "Unknown" })
        {
            html = await Render(plan with { PhaseKind = kind, MaxPicks = -1 });
            var decoded = WebUtility.HtmlDecode(html);
            check(!decoded.Contains("パート登録受付中") && decoded.Contains("パート登録の受付外"),
                kind + " never claims registration is open");
            check(decoded.Contains("設問・観覧・管理者操作の可否は各操作"),
                kind + " does not replace operation-specific authorization");
        }
        html = await Render(plan with { PhaseKind = "Hidden" });
        check(WebUtility.HtmlDecode(html).Contains("非公開（管理者表示）"),
            "Manager-only hidden phase is visibly distinguished from public view");
    }
}