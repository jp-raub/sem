using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Sem.Web.Runtime;

public static class InboxEndpoints
{
    private const int MaxBodyBytes = 4 * 1024;
    private static readonly string[] AllMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void MapApplicationInbox(this WebApplication app)
    {
        app.MapMethods("/api/core/inbox", AllMethods,
            (HttpContext context, InboxService service) => HandleList(context, service));
        app.MapMethods("/api/core/inbox/{action}", AllMethods,
            (HttpContext context, InboxService service, IAntiforgery antiforgery,
                DeploymentSettings settings) => HandleAction(context, service, antiforgery, settings));
    }

    private static async Task HandleList(HttpContext context, InboxService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Allow = "GET";
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        try
        {
            var page = ParsePage(context.Request.Query);
            var result = await service.ListAsync(context, page, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
        }
        catch (UnauthorizedAccessException) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; }
        catch (ValidationException e) { await BadRequest(context, e.Message); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static async Task HandleAction(HttpContext context, InboxService service,
        IAntiforgery antiforgery, DeploymentSettings settings)
    {
        context.Response.Headers.CacheControl = "no-store";
        var action = context.Request.RouteValues["action"]?.ToString();
        if (action is not ("message" or "read"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var allowedMethod = action == "message" ? "GET" : "POST";
        context.Response.Headers.Allow = allowedMethod;
        if (context.Request.Method != allowedMethod)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        try
        {
            if (action == "message")
            {
                var (messageId, expectedActor) = ParseMessageQuery(context.Request.Query);
                InboxService.ValidateId(messageId);
                var detail = await service.ReadDetailAsync(context, expectedActor, messageId, context.RequestAborted);
                await context.Response.WriteAsJsonAsync(detail, context.RequestAborted);
                return;
            }

            if (context.Request.Headers.Origin.Count != 1 ||
                context.Request.Headers.Origin.ToString() != settings.PublicUrl.TrimEnd('/'))
                throw new UnauthorizedAccessException();

            var actor = ParseExpectedActor(context.Request.Query);
            // Validate the current principal before touching the body or antiforgery subsystem.
            _ = await service.RequireActorAsync(context, actor, context.RequestAborted);
            CheckBodyEnvelope(context);
            var csrfValues = context.Request.Headers["X-CSRF-TOKEN"];
            if (csrfValues.Count != 1 || string.IsNullOrWhiteSpace(csrfValues[0]))
                throw new ValidationException("CSRFヘッダーを確認してください。");
            await antiforgery.ValidateRequestAsync(context);

            var id = await ReadIdAsync(context.Request, context.RequestAborted);
            var result = await service.MarkReadAsync(context, actor, id, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
        }
        catch (UnauthorizedAccessException) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; }
        catch (InboxNotFoundException) { context.Response.StatusCode = StatusCodes.Status404NotFound; }
        catch (AntiforgeryValidationException) { await BadRequest(context, "CSRFトークンを確認してください。"); }
        catch (DbUpdateConcurrencyException) { context.Response.StatusCode = StatusCodes.Status409Conflict; }
        catch (DbUpdateException) { context.Response.StatusCode = StatusCodes.Status409Conflict; }
        catch (ValidationException e) { await BadRequest(context, e.Message); }
        catch (JsonException) { await BadRequest(context, "JSON形式を確認してください。"); }
        catch (DecoderFallbackException) { await BadRequest(context, "UTF-8形式を確認してください。"); }
        catch (InvalidDataException) { context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static int ParsePage(IQueryCollection query)
    {
        if (query.Count == 0) return 1;
        if (query.Count != 1 || !query.TryGetValue("page", out var value) || value.Count != 1 ||
            !int.TryParse(value[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var page) ||
            page is < 1 or > InboxService.MaximumPage)
            throw new ValidationException("ページ番号は1～10000で指定してください。");
        return page;
    }

    private static (string Id, string ExpectedActor) ParseMessageQuery(IQueryCollection query)
    {
        if (query.Count != 2 ||
            !query.TryGetValue("id", out var id) || id.Count != 1 ||
            !query.TryGetValue("expectedActor", out var actor) || actor.Count != 1 ||
            string.IsNullOrWhiteSpace(actor[0]) || actor[0]!.Length > 128 ||
            actor[0]!.Any(char.IsControl))
            throw new ValidationException("idとexpectedActorを確認してください。");
        return (id[0] ?? "", actor[0]!);
    }

    private static string ParseExpectedActor(IQueryCollection query)
    {
        if (query.Count != 1 || !query.TryGetValue("expectedActor", out var value) ||
            value.Count != 1 || string.IsNullOrWhiteSpace(value[0]) ||
            value[0]!.Length > 128 || value[0]!.Any(char.IsControl))
            throw new ValidationException("expectedActorを確認してください。");
        return value[0]!;
    }

    private static void CheckBodyEnvelope(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength is > MaxBodyBytes) throw new InvalidDataException();
        if (!HasUtf8JsonContentType(request.ContentType) ||
            request.Headers.ContentEncoding.Count > 1 ||
            request.Headers.ContentEncoding.Count == 1 &&
            !string.Equals(request.Headers.ContentEncoding[0]?.Trim(), "identity", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("UTF-8のapplication/jsonで送信してください。");

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxBodyBytes;
        else if (feature is { IsReadOnly: true } &&
                 (feature.MaxRequestBodySize is not long max || max > MaxBodyBytes))
            throw new InvalidDataException();
    }

    private static bool HasUtf8JsonContentType(string? value)
    {
        if (!MediaTypeHeaderValue.TryParse(value, out var mediaType) ||
            !string.Equals(mediaType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            return false;
        var parameters = mediaType.Parameters.ToArray();
        return parameters.Length == 0 ||
            parameters.Length == 1 &&
            string.Equals(parameters[0].Name, "charset", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parameters[0].Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadIdAsync(HttpRequest request, CancellationToken ct)
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
        var json = StrictUtf8.GetString(bytes);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new ValidationException("UTF-8 BOMは使用できません。");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 4,
            CommentHandling = JsonCommentHandling.Disallow
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ValidationException("JSONオブジェクトが必要です。");

        string? id = null;
        var found = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name != "id" || found || property.Value.ValueKind != JsonValueKind.String)
                throw new ValidationException("id以外の項目や重複・不正な型は指定できません。");
            found = true;
            id = property.Value.GetString();
        }
        if (!found || id is null) throw new ValidationException("idを指定してください。");
        InboxService.ValidateId(id);
        return id;
    }

    private static async Task BadRequest(HttpContext context, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }
}