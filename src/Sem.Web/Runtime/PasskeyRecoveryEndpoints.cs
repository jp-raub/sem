using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public static class PasskeyRecoveryEndpoints
{
    public static async Task Handle(HttpContext context, PasskeyRecoveryService service, IAntiforgery csrf, DeploymentSettings settings)
    {
        context.Response.Headers.CacheControl = "no-store";
        var action = context.Request.RouteValues["action"]?.ToString() ?? "";
        var ct = context.RequestAborted;
        if (action == "passkey-recovery-admin-context")
        {
            if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
            var actor = await service.RequireAdminAsync(context, ct);
            if (context.Request.Query.Count == 0) { await context.Response.WriteAsJsonAsync(new { actorId = actor.Id }, ct); return; }
            var id = Query(context, "id");
            var target = await service.ReadAdminTargetAsync(context, id, ct);
            await context.Response.WriteAsJsonAsync(new { actorId = actor.Id, target }, ct); return;
        }
        var adminAction = action is "passkey-recovery-admin-options" or "passkey-recovery-start" or "passkey-recovery-resend-options" or "passkey-recovery-resend";
        if (!adminAction && action is not ("passkey-recovery-confirm" or "passkey-recovery-options" or "passkey-recovery-complete"))
        { context.Response.StatusCode = 404; return; }
        if (!HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = 405; return; }
        if (context.Request.Headers.Origin.Count != 1 || context.Request.Headers.Origin.ToString() != settings.PublicUrl.TrimEnd('/')) throw new UnauthorizedAccessException();
        ApplicationUser? admin = null;
        if (adminAction)
        {
            var expected = Query(context, "expectedActor");
            admin = await service.RequireAdminAsync(context, ct);
            if (admin.Id != expected) throw new UnauthorizedAccessException();
        }
        else if (context.Request.Query.Count != 0) throw new ValidationException("クエリは指定できません。");
        var max = action is "passkey-recovery-start" or "passkey-recovery-resend" or "passkey-recovery-complete" ? 65536 : 4096;
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = max + 16384;
        if (context.Request.ContentLength > max) { context.Response.StatusCode = 413; return; }
        if (!context.Request.HasJsonContentType()) throw new ValidationException("JSON形式を指定してください。");
        // Require the configured header: never let antiforgery parse a form body.
        var csrfHeader = context.Request.Headers["X-CSRF-TOKEN"];
        if (csrfHeader.Count != 1 || string.IsNullOrWhiteSpace(csrfHeader[0])) throw new ValidationException("CSRFヘッダーを確認してください。");
        await csrf.ValidateRequestAsync(context);
        using var buffer = new MemoryStream(); var block = new byte[4096];
        while (true)
        {
            var n = await context.Request.Body.ReadAsync(block.AsMemory(0, Math.Min(block.Length, max + 1 - (int)buffer.Length)), ct);
            if (n == 0) break;
            buffer.Write(block, 0, n);
            if (buffer.Length > max) { context.Response.StatusCode = 413; return; }
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
        catch (DecoderFallbackException) { throw new ValidationException("UTF-8 JSONを指定してください。"); }
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ValidationException("JSONオブジェクトを指定してください。");
        string[] allowed = action switch
        {
            "passkey-recovery-admin-options" => ["targetUserId", "reason"],
            "passkey-recovery-start" => ["targetUserId", "reason", "credential", "explicitlyConfirmed"],
            "passkey-recovery-resend-options" => ["operationId"],
            "passkey-recovery-resend" => ["operationId", "credential"],
            "passkey-recovery-confirm" => ["operationId", "token"],
            "passkey-recovery-options" => ["userId", "grant"],
            _ => ["userId", "grant", "credential"]
        };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (!names.Add(p.Name) || !allowed.Contains(p.Name, StringComparer.Ordinal)) throw new ValidationException("不明または重複した項目です。");
        string Read(string key, int length)
        {
            if (!doc.RootElement.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.String) throw new ValidationException("入力項目を確認してください。");
            var value = p.GetString()!;
            if (string.IsNullOrWhiteSpace(value) || value.Length > length || value.Any(char.IsControl)) throw new ValidationException("入力項目を確認してください。");
            return value;
        }
        async Task Options(string value)
        {
            using var options = JsonDocument.Parse(value);
            await context.Response.WriteAsJsonAsync(new { publicKey = options.RootElement.Clone() }, ct);
        }
        switch (action)
        {
            case "passkey-recovery-admin-options":
                await Options(await service.CreateAdminProofOptionsAsync(context, admin!, Read("targetUserId", 128), Read("reason", 500), ct)); break;
            case "passkey-recovery-start":
                if (!doc.RootElement.TryGetProperty("explicitlyConfirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True) throw new ValidationException("復旧の影響を確認してください。");
                await context.Response.WriteAsJsonAsync(await service.StartAsync(context, admin!, Read("targetUserId", 128), Read("reason", 500), Read("credential", 50000), true, ct), ct); break;
            case "passkey-recovery-resend-options":
                await Options(await service.CreateResendProofOptionsAsync(context, admin!, Read("operationId", 128), ct)); break;
            case "passkey-recovery-resend":
                await context.Response.WriteAsJsonAsync(await service.ResendAsync(context, admin!, Read("operationId", 128), Read("credential", 50000), ct), ct); break;
            case "passkey-recovery-confirm":
                await context.Response.WriteAsJsonAsync(await service.ConfirmMailAsync(Read("operationId", 128), Read("token", 128), ct), ct); break;
            case "passkey-recovery-options":
                await Options(await service.MakePasskeyCreationOptionsAsync(context, Read("userId", 128), Read("grant", 128), ct)); break;
            case "passkey-recovery-complete":
                await service.CompletePasskeyRegistrationAsync(context, Read("userId", 128), Read("grant", 128), Read("credential", 50000), ct);
                await context.Response.WriteAsJsonAsync(new { message = "パスキーを登録しました。通常のパスキーログインを行ってください。" }, ct); break;
        }
    }
    static string Query(HttpContext context, string name)
    {
        if (context.Request.Query.Count != 1 || !context.Request.Query.TryGetValue(name, out var values) || values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 128 || values[0]!.Any(char.IsControl)) throw new ValidationException("照合情報を確認してください。");
        return values[0]!;
    }
}
