using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

/// <summary>
/// Strict HTTP boundary for the existing anonymous native-passkey login actions.
/// The Identity passkey methods and successful-session contract remain unchanged.
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
        DeploymentSettings settings)
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
        if (!isOptions && !isLogin)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "操作が見つかりません。");
            return;
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
        var expectedOrigin = settings.PublicUrl.TrimEnd('/');
        if (!string.Equals(context.Request.Headers.Origin.ToString(),
                expectedOrigin, StringComparison.Ordinal))
        {
            await WriteError(context, StatusCodes.Status403Forbidden,
                "この送信元からの操作は許可されていません。");
            return;
        }
        if (!context.Request.Headers.TryGetValue("X-CSRF-TOKEN", out var csrfValues) ||
            csrfValues.Count != 1 || string.IsNullOrWhiteSpace(csrfValues[0]))
        {
            await WriteError(context, StatusCodes.Status400BadRequest,
                "ページを再読み込みしてください。");
            return;
        }

        // Apply the server limit before antiforgery reads any part of the body.
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = bodyLimit;

        await antiforgery.ValidateRequestAsync(context);
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
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(options, context.RequestAborted);
            return;
        }

        var result = await signin.PasskeySignInAsync(value);
        if (!result.Succeeded)
            throw new ValidationException("パスキーでログインできませんでした。");
        var authenticated = await users.GetUserAsync(context.User);
        if (authenticated is not { Enabled: true, EmailConfirmed: true })
        {
            await signin.SignOutAsync();
            throw new UnauthorizedAccessException();
        }
        await signin.SignInWithClaimsAsync(authenticated, false,
            [new Claim(PasskeyAdditionService.SessionClaim, SemEndpoints.Secret())]);
        await context.Response.WriteAsJsonAsync(
            new { message = "ログインしました。" }, context.RequestAborted);
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