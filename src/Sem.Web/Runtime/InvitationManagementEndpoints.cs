using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Sem.Web.Runtime;

public static class InvitationManagementEndpoints
{
    private const int MaxRevokeBodyBytes = 4 * 1024;
    private static readonly string[] AllMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void MapInvitationManagement(this WebApplication app)
    {
        app.MapMethods("/api/core/invitations", AllMethods,
            (HttpContext context, InvitationManagementService service) => ListAsync(context, service));
        app.MapMethods("/api/core/invitations/{action}", AllMethods,
            (HttpContext context, InvitationManagementService service, IAntiforgery csrf,
             DeploymentSettings settings) => ActionAsync(context, service, csrf, settings));
    }

    private static async Task ListAsync(HttpContext context, InvitationManagementService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.Headers["Allow"] = "GET";
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        try
        {
            var page = ParsePage(context.Request.Query);
            var result = await service.ListAsync(context, page, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
        catch (ValidationException e)
        {
            await BadRequest(context, e.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static async Task ActionAsync(HttpContext context, InvitationManagementService service,
        IAntiforgery csrf, DeploymentSettings settings)
    {
        context.Response.Headers.CacheControl = "no-store";
        var action = context.Request.RouteValues["action"]?.ToString();
        if (action is not ("revoke" or "issue"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.Headers["Allow"] = "POST";
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        try
        {
            if (context.Request.Headers.Origin.Count != 1 ||
                context.Request.Headers.Origin.ToString() != settings.PublicUrl.TrimEnd('/'))
                throw new UnauthorizedAccessException();
            var expectedActor = ExpectedActor(context.Request.Query);
            await service.RequireActorAsync(context, expectedActor, adminOnly: true, context.RequestAborted);

            CheckBodyEnvelope(context);
            var csrfHeader = context.Request.Headers["X-CSRF-TOKEN"];
            if (csrfHeader.Count != 1 || string.IsNullOrWhiteSpace(csrfHeader[0]))
                throw new ValidationException("CSRFヘッダーを確認してください。");
            await csrf.ValidateRequestAsync(context);

            if (action == "issue")
            {
                var request = await ReadIssueRequestAsync(context.Request, context.RequestAborted);
                var result = await service.IssueAsync(context, expectedActor, request.Purpose,
                    request.Count, request.Days,
                    context.RequestServices.GetRequiredService<IDataProtectionProvider>()
                        .CreateProtector("SEM.Tokens.v1"),
                    settings, context.RequestAborted);
                await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
            }
            else
            {
                var request = await ReadRevokeRequestAsync(context.Request, context.RequestAborted);
                var result = await service.RevokeAsync(context, expectedActor, request.Id, request.Reason,
                    request.ExplicitlyConfirmed, context.RequestAborted);
                await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
            }
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
        catch (InvitationManagementNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (AntiforgeryValidationException)
        {
            await BadRequest(context, "CSRFトークンを確認してください。");
        }
        catch (DbUpdateConcurrencyException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (DbUpdateException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (ValidationException e)
        {
            await BadRequest(context, e.Message);
        }
        catch (JsonException)
        {
            await BadRequest(context, "JSON形式を確認してください。");
        }
        catch (DecoderFallbackException)
        {
            await BadRequest(context, "UTF-8形式を確認してください。");
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static int ParsePage(IQueryCollection query)
    {
        if (query.Count == 0) return 1;
        if (query.Count != 1 || !query.TryGetValue("page", out var value) || value.Count != 1 ||
            !int.TryParse(value[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var page) ||
            page is < 1 or > InvitationManagementService.MaximumPage)
            throw new ValidationException("ページ番号は1～10000で指定してください。");
        return page;
    }

    private static string ExpectedActor(IQueryCollection query)
    {
        if (query.Count != 1 || !query.TryGetValue("expectedActor", out var value) || value.Count != 1 ||
            string.IsNullOrWhiteSpace(value[0]) || value[0]!.Length > 128 || value[0]!.Any(char.IsControl))
            throw new ValidationException("expectedActorを確認してください。");
        return value[0]!;
    }

    private static void CheckBodyEnvelope(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength is > MaxRevokeBodyBytes) throw new InvalidDataException();
        if (!request.HasJsonContentType() || request.Headers.ContentEncoding.Count > 0 &&
            !string.Equals(request.Headers.ContentEncoding.ToString(), "identity", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("application/jsonで送信してください。");

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxRevokeBodyBytes;
        else if (feature is { IsReadOnly: true } &&
                 (feature.MaxRequestBodySize is not long max || max > MaxRevokeBodyBytes))
            throw new InvalidDataException();
    }

    private static async Task<RevokeRequest> ReadRevokeRequestAsync(HttpRequest request, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[2048];
        while (true)
        {
            var remaining = MaxRevokeBodyBytes + 1 - (int)stream.Length;
            if (remaining <= 0) throw new InvalidDataException();
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (stream.Length > MaxRevokeBodyBytes) throw new InvalidDataException();

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
            throw new ValidationException("JSONオブジェクトを指定してください。");

        string? id = null;
        string? reason = null;
        bool? confirmed = null;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!fields.Add(property.Name) || property.Name is not ("id" or "reason" or "explicitlyConfirmed"))
                throw new ValidationException("項目名が重複または不正です。");
            switch (property.Name)
            {
                case "id" when property.Value.ValueKind == JsonValueKind.String:
                    id = property.Value.GetString();
                    break;
                case "reason" when property.Value.ValueKind == JsonValueKind.String:
                    reason = property.Value.GetString();
                    break;
                case "explicitlyConfirmed" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    confirmed = property.Value.GetBoolean();
                    break;
                default:
                    throw new ValidationException("入力の型を確認してください。");
            }
        }
        if (fields.Count != 3 || id is null || reason is null || confirmed is null)
            throw new ValidationException("id、reason、explicitlyConfirmedを指定してください。");
        return new RevokeRequest(id, reason, confirmed.Value);
    }

    private static async Task<IssueRequest> ReadIssueRequestAsync(HttpRequest request, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[2048];
        while (true)
        {
            var remaining = MaxRevokeBodyBytes + 1 - (int)stream.Length;
            if (remaining <= 0) throw new InvalidDataException();
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (stream.Length > MaxRevokeBodyBytes) throw new InvalidDataException();

        var bytes = stream.ToArray();
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new ValidationException("UTF-8 BOMは使用できません。");
        var json = StrictUtf8.GetString(bytes);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 4,
            CommentHandling = JsonCommentHandling.Disallow
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ValidationException("JSONオブジェクトを指定してください。");

        string? purpose = null;
        int? count = null;
        int? days = null;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!fields.Add(property.Name) || property.Name is not ("purpose" or "count" or "days"))
                throw new ValidationException("項目名が重複または不正です。");
            switch (property.Name)
            {
                case "purpose" when property.Value.ValueKind == JsonValueKind.String:
                    purpose = property.Value.GetString();
                    break;
                case "count" when property.Value.ValueKind == JsonValueKind.Number &&
                                  property.Value.TryGetInt32(out var parsedCount):
                    count = parsedCount;
                    break;
                case "days" when property.Value.ValueKind == JsonValueKind.Number &&
                                 property.Value.TryGetInt32(out var parsedDays):
                    days = parsedDays;
                    break;
                default:
                    throw new ValidationException("入力の型を確認してください。");
            }
        }
        if (fields.Count != 3 || purpose is null || count is null || days is null)
            throw new ValidationException("purpose、count、daysを指定してください。");
        return new IssueRequest(purpose, count.Value, days.Value);
    }

    private static async Task BadRequest(HttpContext context, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }

    private sealed record RevokeRequest(string Id, string Reason, bool ExplicitlyConfirmed);
    private sealed record IssueRequest(string Purpose, int Count, int Days);
}
