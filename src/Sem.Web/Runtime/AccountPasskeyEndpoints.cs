using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public static class AccountPasskeyEndpoints
{
    const int MaxRequestBytes = 64 * 1024;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void MapAccountPasskeys(this WebApplication app) =>
        app.MapMethods("/api/account-passkeys/{action}", ["GET", "POST"], Handle);

    static Task Handle(HttpContext context, AccountPasskeyService service,
        PasskeyAdditionService additions, Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> users,
        Microsoft.AspNetCore.Antiforgery.IAntiforgery csrf, DeploymentSettings settings) =>
        HandleCore(context, service, additions, users, csrf, settings, null, legacyEnvelope: false);

    /// <summary>
    /// Preserve the old account-add route names as strict adapters. Authenticated
    /// passkey-options/complete use the email-confirmed addition flow; anonymous
    /// invitation/bootstrap enrollment remains on its existing handler.
    /// </summary>
    public static async Task<bool> TryHandleLegacyAsync(HttpContext context, string legacyAction)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) return false;
        var action = legacyAction switch
        {
            "passkey-add-begin" => "add-begin",
            "passkey-add-reauth" => "add-reauth",
            "passkey-add-resend" => "add-resend",
            "passkey-add-confirm" => "add-confirm",
            "passkey-add-cancel" => "add-cancel",
            "passkey-options" when context.User.Identity?.IsAuthenticated == true => "add-create-options",
            "passkey-complete" when context.User.Identity?.IsAuthenticated == true => "add-complete",
            _ => null
        };
        if (action is null) return false;

        var services = context.RequestServices;
        await HandleCore(context,
            services.GetRequiredService<AccountPasskeyService>(),
            services.GetRequiredService<PasskeyAdditionService>(),
            services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>(),
            services.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>(),
            services.GetRequiredService<DeploymentSettings>(),
            action,
            legacyEnvelope: true);
        return true;
    }

    static async Task HandleCore(HttpContext context, AccountPasskeyService service,
        PasskeyAdditionService additions, Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> users,
        Microsoft.AspNetCore.Antiforgery.IAntiforgery csrf, DeploymentSettings settings,
        string? actionOverride, bool legacyEnvelope)
    {
        SetNoStore(context);
        try
        {
            var action = actionOverride ?? context.Request.RouteValues["action"]?.ToString() ?? "";
            var ct = context.RequestAborted;
            if (HttpMethods.IsGet(context.Request.Method))
            {
                if (action != "list") { await Error(context, 404, "操作が見つかりません。"); return; }
                if (context.Request.Query.Count != 0 || context.Request.ContentLength is > 0)
                { await Error(context, 400, "URLや本文に入力値を含めないでください。"); return; }
                var expectedHeaders = context.Request.Headers["X-Expected-Actor"];
                if (expectedHeaders.Count > 1)
                { await Error(context, 400, "アカウントの照合情報を確認してください。"); return; }
                var expected = expectedHeaders.ToString();
                if (expected.Length > 256 || expected.Any(char.IsControl))
                { await Error(context, 400, "アカウントの照合情報を確認してください。"); return; }
                await context.Response.WriteAsJsonAsync(await service.ListAsync(context, expected.Length == 0 ? null : expected, ct), ct);
                return;
            }
            if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "POSTで送信してください。"); return; }
            if (context.Request.Query.Count != 0) { await Error(context, 400, "URLに入力値を含めないでください。"); return; }
            if (context.Request.Headers.Origin.Count != 1 ||
                context.Request.Headers.Origin.ToString() != settings.PublicUrl.TrimEnd('/'))
                throw new UnauthorizedAccessException();
            await service.ActorAsync(context.User, expectedActor: null, ct: ct);
            var csrfHeader = context.Request.Headers["X-CSRF-TOKEN"];
            if (csrfHeader.Count != 1 || string.IsNullOrWhiteSpace(csrfHeader[0]))
                throw new ValidationException("CSRFヘッダーを確認してください。");
            await csrf.ValidateRequestAsync(context);
            using var document = await ReadStrictJsonAsync(context);
            var root = document.RootElement;
            var allowed = action switch
            {
                "rename" => new[] { "expectedActor", "credentialId", "name" },
                "delete-options" => new[] { "expectedActor", "credentialId" },
                "delete-verify" => new[] { "expectedActor", "proofId", "credential" },
                "delete-confirm" => new[] { "expectedActor", "proofId", "confirmed" },
                "delete-cancel" => new[] { "expectedActor", "proofId" },
                "add-begin" => new[] { "expectedActor" },
                "add-reauth" => new[] { "expectedActor", "id", "credential" },
                "add-resend" or "add-cancel" => new[] { "expectedActor", "id" },
                "add-confirm" => new[] { "expectedActor", "token" },
                "add-create-options" => new[] { "expectedActor" },
                "add-complete" => new[] { "expectedActor", "credential" },
                _ => []
            };
            if (allowed.Length == 0) { await Error(context, 404, "操作が見つかりません。"); return; }
            if (legacyEnvelope) allowed = allowed.Where(property => property != "expectedActor").ToArray();
            RequireOnlyProperties(root, allowed);
            var expectedActor = legacyEnvelope
                ? ((await users.GetUserAsync(context.User))?.Id ?? throw new UnauthorizedAccessException())
                : RequiredString(root, "expectedActor", 256);
            var user = await service.ActorAsync(context.User, expectedActor, ct);

            switch (action)
            {
                case "rename":
                    await service.RenameAsync(context.User, expectedActor,
                        RequiredString(root, "credentialId", 2048), RequiredString(root, "name", 200), ct);
                    await context.Response.WriteAsJsonAsync(new { message = "パスキー名を変更しました。" }, ct);
                    return;
                case "delete-options":
                    await context.Response.WriteAsJsonAsync(await service.BeginDeleteAsync(context, expectedActor,
                        RequiredString(root, "credentialId", 2048), ct), ct);
                    return;
                case "delete-verify":
                    await service.VerifyDeleteAsync(context, expectedActor, RequiredString(root, "proofId", 32),
                        RequiredString(root, "credential", 60_000), ct);
                    await context.Response.WriteAsJsonAsync(new { message = "パスキーを確認しました。削除内容を確認してください。" }, ct);
                    return;
                case "delete-confirm":
                    if (!root.TryGetProperty("confirmed", out var confirmed) ||
                        confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new ValidationException("明示確認を指定してください。");
                    if (!await service.ConfirmDeleteAsync(context, expectedActor,
                            RequiredString(root, "proofId", 32), confirmed.GetBoolean(), ct))
                    {
                        await Error(context, 409, "最後のパスキーは削除できません。別のパスキーを追加してからやり直してください。");
                        return;
                    }
                    await context.Response.WriteAsJsonAsync(new { message = "パスキーを削除しました。" }, ct);
                    return;
                case "delete-cancel":
                    await service.CancelDeleteAsync(context, expectedActor, RequiredString(root, "proofId", 32), ct);
                    await context.Response.WriteAsJsonAsync(new { message = "削除確認を取り消しました。" }, ct);
                    return;
                case "add-begin":
                    await context.Response.WriteAsJsonAsync(await additions.BeginAsync(context, user), ct);
                    return;
                case "add-reauth":
                    await additions.ReauthenticateAsync(context, user, RequiredString(root, "id", 64),
                        RequiredString(root, "credential", 60_000));
                    await context.Response.WriteAsJsonAsync(new { message = "再認証しました。保存済みメールへ確認コードを送ります。" }, ct);
                    return;
                case "add-resend":
                    await additions.ResendAsync(context, user, RequiredString(root, "id", 64));
                    await context.Response.WriteAsJsonAsync(new { message = "確認コードを再送しました。以前のコードは無効になりました。" }, ct);
                    return;
                case "add-confirm":
                    await additions.ConfirmEmailAsync(context, user, RequiredConfirmationCode(root));
                    await context.Response.WriteAsJsonAsync(new { message = "メールを再確認しました。期限内に新しいパスキーを登録してください。" }, ct);
                    return;
                case "add-create-options":
                    var options = await additions.CreationOptionsAsync(context, user);
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(options, StrictUtf8, ct);
                    return;
                case "add-complete":
                    await additions.CompleteAsync(context, user, RequiredString(root, "credential", 60_000));
                    await context.Response.WriteAsJsonAsync(new { message = "パスキーを追加しました。" }, ct);
                    return;
                case "add-cancel":
                    await additions.CancelAsync(context, user, RequiredString(root, "id", 64));
                    await context.Response.WriteAsJsonAsync(new { message = "パスキー追加を取り消しました。" }, ct);
                    return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            await Error(context, 401, "本人の有効なセッションを確認できません。再読み込みしてログイン状態を確認してください。");
        }
        catch (AccountPasskeyConflictException error)
        {
            await Error(context, 409, error.Message);
        }
        catch (ValidationException error)
        {
            await Error(context, 400, error.Message);
        }
        catch (JsonException)
        {
            await Error(context, 400, "JSON形式を確認してください。");
        }
        catch (DecoderFallbackException)
        {
            await Error(context, 400, "本文はUTF-8で送信してください。");
        }
        catch (AntiforgeryValidationException)
        {
            await Error(context, 400, "CSRF確認に失敗しました。ページを再読み込みしてください。");
        }
        catch (DbUpdateConcurrencyException)
        {
            await Error(context, 409, "別の操作で状態が変わりました。最新状態を再取得してください。");
        }
        catch (DbUpdateException)
        {
            await Error(context, 409, "変更を確定できません。最新状態を再取得してください。");
        }
        catch (RequestTooLargeException)
        {
            await Error(context, 413, "送信サイズが上限を超えています。");
        }
    }

    static async Task<JsonDocument> ReadStrictJsonAsync(HttpContext context)
    {
        var type = context.Request.ContentType;
        if (type is null) throw new ValidationException("Content-Typeはapplication/jsonで送信してください。");
        var segments = type.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 1 or > 2 || !segments[0].Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            segments.Length == 2 && !segments[1].Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Content-TypeはUTF-8のapplication/jsonで送信してください。");
        if (context.Request.Headers.ContentEncoding.Count > 0 &&
            !string.Equals(context.Request.Headers.ContentEncoding.ToString(), "identity", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("圧縮された本文は受け付けません。");
        if (context.Request.ContentLength is > MaxRequestBytes) throw new RequestTooLargeException();
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxRequestBytes;
        await using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (read == 0) break;
            if (output.Length + read > MaxRequestBytes) throw new RequestTooLargeException();
            await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
        }
        var text = StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new ValidationException("JSONオブジェクトを送信してください。");
        }
        return document;
    }

    static void RequireOnlyProperties(JsonElement root, string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ValidationException("JSONの項目を確認してください。");
    }

    static string RequiredString(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ValidationException("入力項目を確認してください。");
        var result = value.GetString()!;
        if (result.Length is 0 || result.Length > max || result.Any(char.IsControl))
            throw new ValidationException("入力項目を確認してください。");
        return result;
    }

    static string RequiredConfirmationCode(JsonElement root)
    {
        var result = RequiredString(root, "token", 64);
        if (result.Length != 64 || result.Any(c => !Uri.IsHexDigit(c)))
            throw new ValidationException("メールに届いた64桁の確認コードを入力してください。");
        return result;
    }

    static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    static async Task Error(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }

    sealed class RequestTooLargeException : Exception;
}
