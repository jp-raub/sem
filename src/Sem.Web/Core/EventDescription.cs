using System.ComponentModel.DataAnnotations;
using System.Text.Encodings.Web;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using Markdig;

namespace Sem.Web.Core;

/// <summary>One server-side rendering boundary for both draft preview and persisted event descriptions.</summary>
public static class EventDescription
{
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().DisableHtml().Build();
    private static readonly string[] Tags = ["p","br","strong","b","em","i","u","s","ul","ol","li","h2","h3","h4","blockquote","pre","code","a","hr","table","thead","tbody","tr","th","td"];
    private const string Drop = "script,style,iframe,object,embed,svg,math,form,input,button,textarea,link,meta,base,img,video,audio,template,noscript";
    public static string ValidateFormat(string format) => format is "text" or "markdown" or "html" ? format : throw new ValidationException("概要の入力形式を選択してください。");
    public static string Render(string? source, string? format)
    {
        source ??= "";
        // Stored legacy rows are text. Unknown formats fail closed to encoded text.
        if (format is not ("markdown" or "html"))
            return "<p>"+HtmlEncoder.Default.Encode(source).Replace("\r\n","\n").Replace("\r","\n").Replace("\n","<br>")+"</p>";
        var input = format == "markdown" ? Markdig.Markdown.ToHtml(source, Markdown) : source;
        // Parsing never executes scripts or fetches external resources. Drop active containers
        // including their content before the allow-list sanitizer handles malformed HTML.
        var parser = new HtmlParser();
        var initial = parser.ParseDocument(input);
        foreach(var element in initial.QuerySelectorAll(Drop)) element.Remove();
        foreach(var heading in initial.QuerySelectorAll("h1")) heading.OuterHtml = "<h2>"+heading.InnerHtml+"</h2>";
        var sanitizer = new HtmlSanitizer { KeepChildNodes = true };
        sanitizer.AllowedTags.Clear(); sanitizer.AllowedTags.UnionWith(Tags);
        sanitizer.AllowedAttributes.Clear(); sanitizer.AllowedAttributes.Add("href");
        sanitizer.AllowedCssProperties.Clear(); sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedSchemes.Clear(); sanitizer.AllowedSchemes.UnionWith(["https","http"]);
        var safe = parser.ParseDocument(sanitizer.Sanitize(initial.Body?.InnerHtml ?? ""));
        foreach(var link in safe.QuerySelectorAll("a"))
        {
            var href=link.GetAttribute("href");
            if (!EventLocationLink.IsSafe(href)) link.RemoveAttribute("href");
            else {link.SetAttribute("target","_blank");link.SetAttribute("rel","noopener noreferrer");}
        }
        return safe.Body?.InnerHtml ?? "";
    }
}
