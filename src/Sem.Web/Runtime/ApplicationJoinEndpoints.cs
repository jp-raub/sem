using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public static class ApplicationJoinEndpoints
{
    private const int MaxBodyBytes = 4 * 1024;
    private static readonly string[] AllMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void MapApplicationJoin(this WebApplication app)
    {
        app.MapMethods("/api/join/register", AllMethods,
            (HttpContext context, ApplicationJoinService service, UserManager<ApplicationUser> users,
             NotificationService notifications, DeploymentSettings settings, IAntiforgery csrf) =>
                RegisterAsync(context, service, users, notifications, settings, csrf));
        app.MapMethods("/api/join/confirm", AllMethods,
            (HttpContext context, ApplicationJoinService service, DeploymentSettings settings, IAntiforgery csrf) =>
                ConfirmAsync(context, service, settings, csrf));
    }

    private static async Task RegisterAsync(HttpContext context, ApplicationJoinService service,
        UserManager<ApplicationUser> users, NotificationService notifications, DeploymentSettings settings,
        IAntiforgery csrf)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Allow"] = "POST";
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        try
        {
            await ValidateRequestEnvelopeAsync(context, settings, csrf, requireAnonymous: true);
            var fields = await ReadExactJsonAsync(context.Request, ["invite", "name", "email"], context.RequestAborted);
            var message = await service.BeginApplicationJoinAsync(users, notifications, settings,
                fields["invite"], fields["name"], fields["email"], context.RequestAborted);
            await context.Response.WriteAsJsonAsync(new { message }, context.RequestAborted);
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
        catch (AntiforgeryValidationException)
        {
            await BadRequestAsync(context, "ページを再読み込みしてください。");
        }
        catch (ValidationException e)
        {
            await BadRequestAsync(context, e.Message);
        }
        catch (JsonException)
        {
            await BadRequestAsync(context, "入力形式を確認してください。");
        }
        catch (DecoderFallbackException)
        {
            await BadRequestAsync(context, "UTF-8形式を確認してください。");
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (DbUpdateException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static async Task ConfirmAsync(HttpContext context, ApplicationJoinService service,
        DeploymentSettings settings, IAntiforgery csrf)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Allow"] = "POST";
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        try
        {
            await ValidateRequestEnvelopeAsync(context, settings, csrf, requireAnonymous: true);
            var fields = await ReadExactJsonAsync(context.Request, ["token"], context.RequestAborted);
            var token = fields["token"].Trim();
            if (token.Length is < 1 or > 128 || token.Any(char.IsControl))
                throw new ValidationException("確認トークンを確認してください。");
            var confirmation = await service.ConfirmAndIssueEnrollmentAsync(context, token, context.RequestAborted);
            if (confirmation != ApplicationJoinConfirmationResult.Confirmed)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = ApplicationJoinService.InvalidTicketMessage }, context.RequestAborted);
                return;
            }
            await context.Response.WriteAsJsonAsync(new { message = ApplicationJoinService.EnrollmentMessage }, context.RequestAborted);
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
        catch (AntiforgeryValidationException)
        {
            await BadRequestAsync(context, "ページを再読み込みしてください。");
        }
        catch (ValidationException e)
        {
            await BadRequestAsync(context, e.Message);
        }
        catch (JsonException)
        {
            await BadRequestAsync(context, "入力形式を確認してください。");
        }
        catch (DecoderFallbackException)
        {
            await BadRequestAsync(context, "UTF-8形式を確認してください。");
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (DbUpdateException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static async Task ValidateRequestEnvelopeAsync(HttpContext context, DeploymentSettings settings,
        IAntiforgery csrf, bool requireAnonymous)
    {
        var request = context.Request;
        if (request.Query.Count != 0) throw new ValidationException("クエリパラメーターは指定できません。");
        if (requireAnonymous && context.User.Identity?.IsAuthenticated == true)
            throw new UnauthorizedAccessException();
        if (request.Headers.Origin.Count != 1 ||
            !string.Equals(request.Headers.Origin.ToString(), settings.PublicUrl.TrimEnd('/'), StringComparison.Ordinal))
            throw new UnauthorizedAccessException();

        if (request.ContentLength is > MaxBodyBytes) throw new InvalidDataException();
        if (!request.HasJsonContentType())
            throw new ValidationException("application/jsonで送信してください。");

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxBodyBytes;
        else if (feature is { IsReadOnly: true } &&
                 (feature.MaxRequestBodySize is not long max || max > MaxBodyBytes))
            throw new InvalidDataException();

        var csrfHeader = request.Headers["X-CSRF-TOKEN"];
        if (csrfHeader.Count != 1 || string.IsNullOrWhiteSpace(csrfHeader[0]))
            throw new ValidationException("CSRFヘッダーを確認してください。");
        await csrf.ValidateRequestAsync(context);
    }

    private static async Task<Dictionary<string, string>> ReadExactJsonAsync(HttpRequest request,
        string[] expected, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[2048];
        while (true)
        {
            var remaining = MaxBodyBytes + 1 - (int)stream.Length;
            if (remaining <= 0) throw new InvalidDataException();
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (stream.Length > MaxBodyBytes) throw new InvalidDataException();
        var bytes = stream.ToArray();
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) throw new ValidationException("UTF-8 BOMは使用できません。");
        var text = StrictUtf8.GetString(bytes);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            MaxDepth = 4,
            CommentHandling = JsonCommentHandling.Disallow
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ValidationException("JSONオブジェクトを指定してください。");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || result.ContainsKey(property.Name))
                throw new ValidationException("項目名が重複または不正です。");
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new ValidationException("文字列を指定してください。");
            result.Add(property.Name, property.Value.GetString() ?? "");
        }
        if (result.Count != expected.Length)
            throw new ValidationException("必須項目を確認してください。");
        return result;
    }

    private static async Task BadRequestAsync(HttpContext context, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }
}
