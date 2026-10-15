using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
namespace Sem.Web.Runtime;

public static class EmailChangeEndpoints
{
    public static async Task Handle(HttpContext context, EmailChangeService service, IAntiforgery csrf, DeploymentSettings settings)
    {
        var action = context.Request.RouteValues["action"]?.ToString() ?? "";
        var get = HttpMethods.IsGet(context.Request.Method);
        if (action == "email-change-status")
        {
            if (!get) { context.Response.StatusCode = 405; return; }
            if (context.Request.Query.Count != 0) throw new ValidationException("クエリは指定できません。");
            await context.Response.WriteAsJsonAsync(await service.Status(context.User, context.RequestAborted)); return;
        }
        if (get) { context.Response.StatusCode = 405; return; }
        if (action is not ("email-change-begin" or "email-change-reauth-options" or "email-change-reauth" or "email-change-cancel" or "email-change-confirm"))
        { context.Response.StatusCode = 404; return; }
        if (context.Request.Headers.Origin.Count != 1 || context.Request.Headers.Origin.ToString() != settings.PublicUrl.TrimEnd('/'))
            throw new UnauthorizedAccessException();
        if (action == "email-change-confirm")
        { if (context.Request.Query.Count != 0) throw new ValidationException("クエリは指定できません。"); }
        else
        {
            var actor = await service.Actor(context.User, context.RequestAborted);
            if (context.Request.Query.Count != 1 || !context.Request.Query.TryGetValue("expectedActor", out var expected) ||
                expected.Count != 1 || string.IsNullOrEmpty(expected[0]) || expected[0]!.Length > 128 || expected[0]!.Any(char.IsControl))
                throw new ValidationException("操作者の照合情報を確認してください。");
            if (expected[0] != actor.Id) throw new UnauthorizedAccessException();
        }
        await csrf.ValidateRequestAsync(context);
        var max = action == "email-change-reauth" ? 65536 : 4096;
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = max + 16384;
        if (context.Request.ContentLength > max) { context.Response.StatusCode = 413; return; }
        if (!context.Request.HasJsonContentType()) throw new ValidationException("JSON形式で送信してください。");
        using var buffer = new MemoryStream(); var block = new byte[4096];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(block.AsMemory(0, Math.Min(block.Length, max + 1 - (int)buffer.Length)), context.RequestAborted);
            if (read == 0) break;
            buffer.Write(block, 0, read);
            if (buffer.Length > max) { context.Response.StatusCode = 413; return; }
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
        catch (DecoderFallbackException) { throw new ValidationException("UTF-8 JSONを指定してください。"); }
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ValidationException("JSONオブジェクトを指定してください。");
        var allowed = action switch { "email-change-begin" => new[] { "newEmail" },
            "email-change-reauth" => ["operationId", "credential"], "email-change-confirm" => ["operationId", "token"],
            _ => ["operationId"] };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (!names.Add(p.Name) || !allowed.Contains(p.Name)) throw new ValidationException("不明または重複した項目です。");
        string Read(string key, int length)
        {
            if (!doc.RootElement.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String)
                throw new ValidationException("入力項目を確認してください。");
            var value = property.GetString()!;
            if (value.Length is 0 || value.Length > length || value.Any(char.IsControl)) throw new ValidationException("入力項目を確認してください。");
            return value;
        }
        var ct = context.RequestAborted;
        if (action == "email-change-begin")
        { await context.Response.WriteAsJsonAsync(await service.Begin(context.User, Read("newEmail", 254), ct)); return; }
        var id = Read("operationId", 36);
        if (!Guid.TryParseExact(id, "D", out _)) throw new ValidationException("操作IDを確認してください。");
        if (action == "email-change-reauth-options")
        { await context.Response.WriteAsJsonAsync(await service.Options(context.User, id, ct)); return; }
        if (action == "email-change-reauth") await service.Reauthenticate(context.User, id, Read("credential", 60000), ct);
        else if (action == "email-change-cancel") await service.Cancel(context.User, id, ct);
        else await service.Confirm(id, Read("token", 64), ct);
        await context.Response.WriteAsJsonAsync(new { message = action switch {
            "email-change-confirm" => "メールアドレスを変更しました。通常セッションは発行していません。パスキーで再ログインしてください。",
            "email-change-cancel" => "変更を取り消しました。", _ => "確認メールを送信待ちに登録しました。再送しても元の期限は延長しません。" } });
    }
}
