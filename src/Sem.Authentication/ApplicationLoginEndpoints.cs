using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Sem.Web.Data;

namespace Sem.Authentication;

/// <summary>
/// 保全版のHTTP境界を標準Identityログインへ再接続する。画面・業務APIは含めない。
/// </summary>
public static class ApplicationLoginEndpoints
{
    const int OptionsBodyLimit = 4 * 1024;
    const int LoginBodyLimit = 64 * 1024;
    const int MaxCredentialLength = 50_000;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task Handle(
        HttpContext context,
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signin,
        IAntiforgery antiforgery,
        AuthenticationSettings settings)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.Headers.Allow = "POST";
            await WriteError(context, StatusCodes.Status405MethodNotAllowed,
                "この操作ではPOSTを使用してください。");
            return;
        }
        if (context.Request.QueryString.HasValue)
        {
            await WriteError(context, StatusCodes.Status400BadRequest,
                "クエリ文字列は使用できません。");
            return;
        }

        var action = context.Request.RouteValues["action"]?.ToString();
        var isOptions = action == "login-options";
        var isLogin = action == "login";
        var isLogout = action == "logout";
        if (!isOptions && !isLogin && !isLogout)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "操作が見つかりません。");
            return;
        }

        // メール・Cookie・credential・例外本文等の秘密値を記録しない。
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Sem.LoginBoundary");
        var diagnosticId = Guid.NewGuid().ToString("N");
        context.Response.Headers["X-SEM-Login-Diagnostic"] = diagnosticId;
        var stageAction = isOptions ? "login-options" : isLogout ? "logout" : "login";
        void Diagnose(string stage, string reason)
        {
            logger.LogInformation(new EventId(4301, "LoginBoundary"),
                "SEM_LOGIN_BOUNDARY action={Action} stage={Stage} reason={Reason} diagnostic={DiagnosticId}",
                stageAction, stage, reason, diagnosticId);
        }

        var bodyLimit = isOptions ? OptionsBodyLimit : LoginBodyLimit;
        if (context.Request.ContentLength is long contentLength && contentLength > bodyLimit)
        {
            await WriteError(context, StatusCodes.Status413PayloadTooLarge,
                "リクエスト本文が大きすぎます。");
            return;
        }
        if (!HasJsonContentType(context.Request.ContentType))
        {
            await WriteError(context, StatusCodes.Status415UnsupportedMediaType,
                "application/jsonで送信してください。");
            return;
        }
        var contentEncoding = context.Request.Headers["Content-Encoding"];
        if (contentEncoding.Count > 1 ||
            contentEncoding.Count == 1 &&
            !string.Equals(contentEncoding[0]?.Trim(), "identity", StringComparison.OrdinalIgnoreCase))
        {
            await WriteError(context, StatusCodes.Status415UnsupportedMediaType,
                "圧縮されたリクエスト本文は使用できません。");
            return;
        }
        var expectedOrigin = settings.PublicOrigin;
        if (!string.Equals(context.Request.Headers.Origin.ToString(),
                expectedOrigin, StringComparison.Ordinal))
        {
            var origins = context.Request.Headers.Origin;
            Diagnose("origin-rejected", origins.Count == 0 ? "missing" : origins.Count != 1 ? "multiple" :
                origins[0] == "null" ? "opaque" : "mismatch");
            await WriteError(context, StatusCodes.Status403Forbidden,
                "この送信元からの操作は許可されていません。");
            return;
        }
        if (!context.Request.Headers.TryGetValue("X-CSRF-TOKEN", out var csrfValues) ||
            csrfValues.Count != 1 || string.IsNullOrWhiteSpace(csrfValues[0]))
        {
            Diagnose("csrf-rejected", "header-missing-or-invalid");
            await WriteError(context, StatusCodes.Status400BadRequest,
                "ページを再読み込みしてください。");
            return;
        }

        // CSRF検証より前に本文サイズを制限する。
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = bodyLimit;

        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            Diagnose("csrf-rejected", "validation-failed");
            throw;
        }
        Diagnose("request-boundary-passed", "origin-and-csrf-valid");
        byte[] bytes;
        try
        {
            bytes = await ReadBoundedBodyAsync(context.Request, bodyLimit,
                context.RequestAborted);
        }
        catch (BodyTooLargeException)
        {
            await WriteError(context, StatusCodes.Status413PayloadTooLarge,
                "リクエスト本文が大きすぎます。");
            return;
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
            when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await WriteError(context, StatusCodes.Status413PayloadTooLarge,
                "リクエスト本文が大きすぎます。");
            return;
        }

        string json;
        try { json = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new JsonException(); }
        if (json.Length > 0 && json[0] == '\uFEFF')
            throw new JsonException();

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4
        });
        if (isLogout)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any())
                throw new ValidationException("入力形式を確認してください。");
            var db = context.RequestServices.GetRequiredService<ApplicationDbContext>();
            var identity = context.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value;
            await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
            if (!await AuthenticationSession.ValidAsync(db, context.User, identity, context.RequestAborted))
                throw new UnauthorizedAccessException();
            var id = AuthenticationSession.SingleClaim(context.User, identity.ClaimsIdentity.UserIdClaimType)!;
            var hash = AuthenticationSession.Hash(AuthenticationSession.SingleClaim(context.User, AuthenticationSession.SessionClaim)!);
            db.RevokedAuthSessions.Add(new Sem.Web.Core.RevokedAuthSession {
                TokenHash = hash, UserId = id, RevokedAt = DateTimeOffset.UtcNow.ToString("O")
            });
            db.PushDevices.RemoveRange(await db.PushDevices.Where(d => d.UserId == id && d.SessionHash == hash).ToListAsync(context.RequestAborted));
            await db.SaveChangesAsync(context.RequestAborted);
            await transaction.CommitAsync(context.RequestAborted);
            await signin.SignOutAsync();
            await context.Response.WriteAsJsonAsync(new {message="ログアウトしました。"}, context.RequestAborted);
            Diagnose("session-revoked", "completed");
            return;
        }
        var value = ReadSingleStringProperty(document.RootElement,
            isOptions ? "email" : "credential",
            isOptions ? 254 : MaxCredentialLength);

        if (isOptions)
        {
            var email = value.ToLowerInvariant();
            var owner = await users.FindByEmailAsync(email);
            if (owner is not { Enabled: true, EmailConfirmed: true })
                throw new ValidationException("ログインできませんでした。");
            var options = await signin.MakePasskeyRequestOptionsAsync(owner);
            using var requestOptions = JsonDocument.Parse(options);
            var attempts = context.RequestServices.GetRequiredService<LoginAttemptStore>();
            var token = attempts.Issue(requestOptions.RootElement.GetProperty("challenge").GetString()!);
            if (token is null) {
                await WriteError(context, StatusCodes.Status429TooManyRequests, "時間をおいてお試しください。");
                return;
            }
            context.Response.Cookies.Append(LoginAttemptStore.CookieName, token, LoginAttemptStore.CookieOptions());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(options, context.RequestAborted);
            Diagnose("options-issued", "completed");
            return;
        }

        var attemptsForLogin = context.RequestServices.GetRequiredService<LoginAttemptStore>();
        string challenge;
        try { challenge = LoginAttemptStore.ReadChallenge(value); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) {
            throw new ValidationException("パスキーでログインできませんでした。");
        }
        context.Response.Cookies.Delete(LoginAttemptStore.CookieName, LoginAttemptStore.CookieOptions());
        if (!attemptsForLogin.Consume(context.Request.Cookies[LoginAttemptStore.CookieName], challenge))
            throw new ValidationException("パスキーでログインできませんでした。");
        // 同一署名・Cookieの並行再送も、この消費を通過するのは1件だけ。
        SignInResult result;
        try { result = await signin.PasskeySignInAsync(value); }
        catch (InvalidOperationException) {
            Diagnose("credential-rejected", "native-state-invalid");
            throw new ValidationException("パスキーでログインできませんでした。");
        }
        if (!result.Succeeded)
        {
            Diagnose("credential-rejected", "identity-refused");
            throw new ValidationException("パスキーでログインできませんでした。");
        }
        var authenticated = await users.GetUserAsync(context.User);
        if (authenticated is not { Enabled: true, EmailConfirmed: true })
        {
            await signin.SignOutAsync();
            throw new UnauthorizedAccessException();
        }
        await signin.SignInWithClaimsAsync(authenticated, false,
            [new Claim(AuthenticationSession.SessionClaim, AuthenticationSession.NewSession())]);
        await context.Response.WriteAsJsonAsync(
            new { message = "ログインしました。" }, context.RequestAborted);
        Diagnose("session-issued", "completed");
    }

    static bool HasJsonContentType(string? value)
    {
        if (!MediaTypeHeaderValue.TryParse(value, out var mediaType) ||
            !string.Equals(mediaType.MediaType, "application/json",
                StringComparison.OrdinalIgnoreCase))
            return false;
        var parameters = mediaType.Parameters.ToArray();
        return parameters.Length == 0 ||
            parameters.Length == 1 &&
            string.Equals(parameters[0].Name, "charset", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parameters[0].Value?.Trim('"'), "utf-8",
                StringComparison.OrdinalIgnoreCase);
    }

    static string ReadSingleStringProperty(JsonElement root, string expectedName, int maxLength)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ValidationException("入力形式を確認してください。");
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 1 || properties[0].Name != expectedName ||
            properties[0].Value.ValueKind != JsonValueKind.String)
            throw new ValidationException("入力形式を確認してください。");
        var value = properties[0].Value.GetString()?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > maxLength ||
            value.Any(char.IsControl))
            throw new ValidationException("入力形式を確認してください。");
        return value;
    }

    static async Task<byte[]> ReadBoundedBodyAsync(
        HttpRequest request, int maxBytes, CancellationToken ct)
    {
        using var output = new MemoryStream(Math.Min(maxBytes, 4096));
        var buffer = new byte[Math.Min(maxBytes + 1, 4096)];
        while (true)
        {
            var remaining = maxBytes + 1 - checked((int)output.Length);
            if (remaining <= 0)
                throw new BodyTooLargeException();
            var read = await request.Body.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            if (output.Length > maxBytes)
                throw new BodyTooLargeException();
        }
        return output.ToArray();
    }

    static async Task WriteError(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message },
            context.RequestAborted);
    }

    sealed class BodyTooLargeException : Exception { }
}