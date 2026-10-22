using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

namespace Sem.Web.Runtime;

public static class DeviceNotificationEndpoints
{
    private const int MaxBodyBytes = 16 * 1024;
    private static readonly string[] AllMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void MapDeviceNotifications(this WebApplication app)
    {
        app.MapMethods("/api/core/device-notifications", AllMethods,
            (HttpContext context, DeviceNotificationService service, DeploymentSettings settings) =>
                HandleRoot(context, service, settings));
        app.MapMethods("/api/core/device-notifications/{action}", AllMethods,
            (HttpContext context, DeviceNotificationService service, IAntiforgery csrf, DeploymentSettings settings) =>
                HandleAction(context, service, csrf, settings));
        // Exact routes take precedence over the legacy catch-all. Keep the old
        // browser contract, but never retain a second subscription mutation path.
        app.MapMethods("/api/subscribe", AllMethods,
            (HttpContext context, DeviceNotificationService service, IAntiforgery csrf, DeploymentSettings settings) =>
                HandleAction(context, service, csrf, settings, "register"));
        app.MapMethods("/api/unsubscribe", AllMethods,
            (HttpContext context, DeviceNotificationService service, IAntiforgery csrf, DeploymentSettings settings) =>
                HandleAction(context, service, csrf, settings, "unregister"));
    }

    private static async Task HandleRoot(HttpContext context, DeviceNotificationService service,
        DeploymentSettings settings)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Allow"] = "GET";
        if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
        try
        {
            if (context.Request.Query.Count != 0) throw new ValidationException("クエリパラメーターは指定できません。");
            if (context.Request.Headers.Origin.Count > 0 &&
                (context.Request.Headers.Origin.Count != 1 || context.Request.Headers.Origin.ToString() != settings.PublicUrl.TrimEnd('/')))
                throw new UnauthorizedAccessException();
            var result = await service.GetInfoAsync(context, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
        }
        catch (UnauthorizedAccessException) { context.Response.StatusCode = 401; }
        catch (ValidationException e) { await BadRequest(context, e.Message); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static async Task HandleAction(HttpContext context, DeviceNotificationService service,
        IAntiforgery csrf, DeploymentSettings settings, string? legacyAction = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        var action = legacyAction ?? context.Request.RouteValues["action"]?.ToString();
        context.Response.Headers["Allow"] = "POST";
        if (action is not ("status" or "register" or "unregister"))
        { context.Response.StatusCode = 404; return; }
        if (!HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = 405; return; }
        try
        {
            var query = context.Request.Query;
            string? expectedActor = null;
            if (legacyAction is not null)
            {
                // The old sem.js does not send expectedActor. Its mutation still
                // requires the live actor, CSRF and stored subscription key proof.
                if (query.Count != 0) throw new ValidationException("クエリパラメーターは指定できません。");
            }
            else
            {
                if (query.Count != 1 || !query.TryGetValue("expectedActor", out var expected) || expected.Count != 1 ||
                    string.IsNullOrWhiteSpace(expected[0]) || expected[0]!.Length > 128 || expected[0]!.Any(char.IsControl))
                    throw new ValidationException("expectedActorを確認してください。");
                expectedActor = expected[0];
            }
            if (context.Request.Headers.Origin.Count != 1 ||
                context.Request.Headers.Origin.ToString() != settings.PublicUrl.TrimEnd('/'))
                throw new UnauthorizedAccessException();

            var actor = await service.RequireActorAsync(context, context.RequestAborted);
            if (expectedActor is not null && !string.Equals(expectedActor, actor.User.Id, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
            CheckBodyEnvelope(context);
            var csrfHeader = context.Request.Headers["X-CSRF-TOKEN"];
            if (csrfHeader.Count != 1 || string.IsNullOrWhiteSpace(csrfHeader[0]))
                throw new ValidationException("CSRFトークンが必要です。");
            await csrf.ValidateRequestAsync(context);
            var body = await ReadBodyAsync(context.Request, context.RequestAborted);
            var fields = ParseExactObject(body, action switch
            {
                "status" or "unregister" => ["endpoint"],
                "register" => ["endpoint", "p256dh", "auth"],
                _ => []
            });
            var endpoint = Required(fields, "endpoint", 2048);
            DeviceNotificationResult result = action switch
            {
                "status" => await service.StatusAsync(context, endpoint, context.RequestAborted),
                "register" => await service.RegisterAsync(context, endpoint,
                    Required(fields, "p256dh", 128), Required(fields, "auth", 128), context.RequestAborted),
                "unregister" => await service.UnregisterAsync(context, endpoint, context.RequestAborted),
                _ => throw new ValidationException("操作を確認してください.")
            };
            if (legacyAction is null) await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
            else await context.Response.WriteAsJsonAsync(new
            {
                result.ActorId, result.Registered,
                message = action == "register" ? "端末を登録しました。" : "端末の通知を解除しました。"
            }, context.RequestAborted);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Match the old mutation endpoint's sanitized conflict response.
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new { error = "変更を確定できません。最新状態を取得してください。" }, context.RequestAborted);
        }
        catch (UnauthorizedAccessException) { context.Response.StatusCode = 401; }
        catch (AntiforgeryValidationException) { await BadRequest(context, "CSRFトークンを確認してください。"); }
        catch (ValidationException e) { await BadRequest(context, e.Message); }
        catch (JsonException) { await BadRequest(context, "JSON形式を確認してください。"); }
        catch (DecoderFallbackException) { await BadRequest(context, "UTF-8形式を確認してください。"); }
        catch (InvalidDataException) { context.Response.StatusCode = 413; }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static void CheckBodyEnvelope(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength is > MaxBodyBytes) throw new InvalidDataException();
        if (!request.HasJsonContentType() || request.Headers.ContentEncoding.Count > 0 &&
            !string.Equals(request.Headers.ContentEncoding.ToString(), "identity", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("application/jsonで送信してください。");
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxBodyBytes;
        else if (feature is { IsReadOnly: true } &&
                 (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > MaxBodyBytes))
            throw new ValidationException("リクエストサイズ制限を適用できません。");
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
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
        _ = StrictUtf8.GetString(bytes);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) throw new ValidationException("UTF-8 BOMは使用できません。");
        return bytes;
    }

    private static Dictionary<string, string> ParseExactObject(byte[] bytes, string[] expected)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ValidationException("JSONオブジェクトが必要です。");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !result.TryAdd(property.Name, ""))
                throw new ValidationException("項目名が重複または不正です。");
            if (property.Value.ValueKind != JsonValueKind.String) throw new ValidationException("文字列を指定してください。");
            result[property.Name] = property.Value.GetString() ?? "";
        }
        if (result.Count != expected.Length) throw new ValidationException("必須項目を確認してください。");
        return result;
    }

    private static string Required(Dictionary<string, string> fields, string name, int max)
    {
        var value = fields[name];
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            throw new ValidationException("入力項目を確認してください。");
        return value;
    }

    private static async Task BadRequest(HttpContext context, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }
}
