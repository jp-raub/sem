using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
public static class CoreEndpoints
{
    // A 1 MiB UTF-8 CSV can expand sixfold when JSON-escaped; the 7 MiB envelope
    // leaves room for the fixed apply fields while keeping the in-memory read bounded.
    private const int EventImportRequestMaxBytes = 7 * 1024 * 1024;
    private static readonly JsonSerializerOptions EventImportJsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<T> ReadEventImportRequestAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType()) throw new ValidationException("JSON形式で送信してください。");
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = EventImportRequestMaxBytes + 1 - (int)body.Length;
            if (remaining <= 0) throw new EventImportPayloadTooLargeException();
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) break;
            await body.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (body.Length > EventImportRequestMaxBytes) throw new EventImportPayloadTooLargeException();
        return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), EventImportJsonOptions)
            ?? throw new ValidationException("入力が必要です。");
    }

    private sealed class EventImportPayloadTooLargeException : Exception { }
    public static void MapCoreFoundation(this WebApplication app) => app.MapMethods("/api/core/{action}", ["GET", "POST"],
        async (HttpContext ctx, CoreFoundationService service, IAntiforgery csrf, DeploymentSettings deployment, ManualPushRuntime manualRuntime, IEventPushTransport pushTransport) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            try
            {
                var action = ctx.Request.RouteValues["action"]?.ToString();
                var post = HttpMethods.IsPost(ctx.Request.Method);
                var eventImport = action is "event-import-preview" or "event-import-apply";
                var eventImportSample = action == "event-import-sample";
                var eventImportContext = action == "event-import-context";
                var themeAppearanceRead = action == "theme-appearance";
                var themeAppearanceWrite = action is "theme-personal" or "theme-event" or "theme-site" or
                    "theme-catalog-save" or "theme-catalog-delete";
                if (themeAppearanceRead && !HttpMethods.IsGet(ctx.Request.Method) ||
                    themeAppearanceWrite && !post)
                {
                    ctx.Response.StatusCode = 405;
                    return;
                }
                if (eventImportSample) ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (eventImportSample && !HttpMethods.IsGet(ctx.Request.Method)) { ctx.Response.StatusCode = 405; return; }
                if (eventImportContext && !HttpMethods.IsGet(ctx.Request.Method)) { ctx.Response.StatusCode = 405; return; }
                if (eventImport && !post) { ctx.Response.StatusCode = 405; return; }
                string? themeEventId = null;
                string? themeCatalogId = null;
                if (themeAppearanceRead)
                {
                    var eventId = OptionalThemeQueryValue(ctx.Request.Query, "id");
                    var appearance = await service.ReadThemeAppearanceAsync(ctx.User, eventId, ctx.RequestAborted);
                    await ctx.Response.WriteAsJsonAsync(new ThemeAppearanceHttpResponse(eventId, appearance));
                    return;
                }
                if (action == "profile-registration-history")
                {
                    // This endpoint is read-only; reject writes before inspecting any request body.
                    if (!HttpMethods.IsGet(ctx.Request.Method)) { ctx.Response.StatusCode = 405; return; }
                    var ids = ctx.Request.Query["id"];
                    if (ids.Count != 1 || string.IsNullOrEmpty(ids[0]) || ids[0]!.Length > 128 || ids[0]!.Any(char.IsControl))
                        throw new ValidationException("プロフィールを確認してください。");
                    await ctx.Response.WriteAsJsonAsync(
                        await service.ReadProfileRegistrationHistoryAsync(ctx.User, ids[0]!, ctx.RequestAborted));
                    return;
                }
                if (eventImportContext)
                {
                    var idValues = ctx.Request.Query["id"];
                    if (idValues.Count != 1 || string.IsNullOrEmpty(idValues[0])) throw new ValidationException("イベントを確認してください。");
                    await ctx.Response.WriteAsJsonAsync(await service.ReadEventImportContextAsync(ctx.User, idValues[0]!, ctx.RequestAborted));
                    return;
                }
                if (eventImportSample)
                {
                    var idValues = ctx.Request.Query["id"];
                    if (idValues.Count != 1 || string.IsNullOrEmpty(idValues[0])) throw new ValidationException("イベントを確認してください。");
                    var csv = await service.ReadEventImportSampleAsync(ctx.User, idValues[0]!, ctx.RequestAborted);
                    ctx.Response.ContentType = "text/csv; charset=utf-8";
                    ctx.Response.Headers["Content-Disposition"] = "attachment; filename=event-import-sample.csv";
                    await ctx.Response.WriteAsync(csv, System.Text.Encoding.UTF8, ctx.RequestAborted);
                    return;
                }
                string? eventImportId = null;
                if (post)
                {
                    if (ctx.Request.Headers.Origin.ToString() != deployment.PublicUrl.TrimEnd('/')) throw new UnauthorizedAccessException();
                    if (themeAppearanceWrite)
                    {
                        if (action == "theme-event") themeEventId = RequiredThemeQueryValue(ctx.Request.Query, "id");
                        if (action == "theme-catalog-save") themeCatalogId = OptionalThemeQueryValue(ctx.Request.Query, "id");
                        if (action == "theme-catalog-delete") themeCatalogId = RequiredThemeQueryValue(ctx.Request.Query, "id");
                        var expectedActor = RequiredThemeQueryValue(ctx.Request.Query, "expectedActor");
                        await service.AuthorizeThemeAppearanceRequestAsync(ctx.User, action!, themeEventId, ctx.RequestAborted);
                        var cookieActor = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                        if (string.IsNullOrEmpty(cookieActor) || !string.Equals(expectedActor, cookieActor, StringComparison.Ordinal))
                            throw new UnauthorizedAccessException();
                    }
                    if (eventImport)
                    {
                        var idValues = ctx.Request.Query["id"];
                        if (idValues.Count != 1 || string.IsNullOrEmpty(idValues[0])) throw new ValidationException("イベントを確認してください。");
                        eventImportId = idValues[0]!;
                        // Fail closed on the current cookie principal before antiforgery or any body handling.
                        await service.AuthorizeEventImportRequestAsync(ctx.User, eventImportId, ctx.RequestAborted);
                        var expectedActor = ctx.Request.Query["expectedActor"];
                        if (expectedActor.Count > 0)
                        {
                            if (expectedActor.Count != 1 || string.IsNullOrEmpty(expectedActor[0]))
                                throw new ValidationException("操作者を確認してください。");
                            var actorId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                            if (string.IsNullOrEmpty(actorId) || expectedActor[0] != actorId)
                                throw new UnauthorizedAccessException();
                        }
                    }
                    var size = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
                    if (size is { IsReadOnly: false })
                    {
                        // Leave Kestrel chunk headroom while bounded readers retain their probe limit, so they can return a
                        // deterministic 413 without buffering beyond their effective request limits.
                        size.MaxRequestBodySize = eventImport ? EventImportRequestMaxBytes + 16 * 1024L :
                            themeAppearanceWrite ? ThemeAppearanceRequestMaxBytes + 16 * 1024L : 65536;
                    }
                    await csrf.ValidateRequestAsync(ctx);
                    if (eventImport && ctx.Request.ContentLength > EventImportRequestMaxBytes) throw new EventImportPayloadTooLargeException();
                }
                if (eventImport)
                {
                    // Non-POST methods were returned as 405 above; POST validated this before any body reads.
                    var id = eventImportId!;
                    if (action == "event-import-preview")
                    {
                        var request = await ReadEventImportRequestAsync<EventImportPreviewRequest>(ctx.Request, ctx.RequestAborted);
                        if (request.Csv is null) throw new ValidationException("CSVを指定してください。");
                        await ctx.Response.WriteAsJsonAsync(await service.ReadEventImportPreviewAsync(ctx.User, id, request.Csv, ctx.RequestAborted));
                    }
                    else
                    {
                        var request = await ReadEventImportRequestAsync<EventImportApply>(ctx.Request, ctx.RequestAborted);
                        await ctx.Response.WriteAsJsonAsync(await service.ApplyEventImportAsync(ctx.User, id, request, ctx.RequestAborted));
                    }
                    return;
                }
                if (themeAppearanceWrite)
                {
                    if (action is "theme-personal" or "theme-event" or "theme-site")
                    {
                        var edit = await ReadThemeAppearanceRequestAsync<ThemePreferenceHttpEdit>(ctx.Request, ctx.RequestAborted);
                        ThemePreferenceView saved = action switch
                        {
                            "theme-personal" => await service.SaveUserThemePreferenceAsync(ctx.User, edit.ToEdit(), ctx.RequestAborted),
                            "theme-event" => await service.SaveEventThemePreferenceAsync(ctx.User, themeEventId!, edit.ToEdit(), ctx.RequestAborted),
                            _ => await service.SaveSiteThemePreferenceAsync(ctx.User, edit.ToEdit(), ctx.RequestAborted)
                        };
                        await ctx.Response.WriteAsJsonAsync(saved);
                        return;
                    }
                    if (action == "theme-catalog-save")
                    {
                        var edit = await ReadThemeAppearanceRequestAsync<ThemeCatalogHttpEdit>(ctx.Request, ctx.RequestAborted);
                        await ctx.Response.WriteAsJsonAsync(await service.AddOrUpdateThemeAsync(
                            ctx.User, themeCatalogId, edit.ToEdit(), ctx.RequestAborted));
                        return;
                    }
                    if (action == "theme-catalog-delete")
                    {
                        var edit = await ReadThemeAppearanceRequestAsync<ThemeCatalogDeleteHttpEdit>(ctx.Request, ctx.RequestAborted);
                        await service.DeleteThemeAsync(ctx.User, themeCatalogId!, edit.RowVersion, ctx.RequestAborted);
                        await ctx.Response.WriteAsJsonAsync(new { message = "削除しました。" });
                        return;
                    }
                }
                if (action == "manual-push-control") {
                    if (post) { ctx.Response.StatusCode = 405; return; }
                    var view = await service.ReadManualPushActivationAsync(ctx.User, ctx.RequestAborted);
                    var actorId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(actorId)) throw new UnauthorizedAccessException();
                    await ctx.Response.WriteAsJsonAsync(new ManualPushControlContext(actorId, view.Enabled, view.Epoch, manualRuntime.DeliveryConnected, pushTransport.Configured));
                    return;
                }
                if (action == "manual-push-control-change") {
                    if (!post) { ctx.Response.StatusCode = 405; return; }
                    var actorId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(actorId) || ctx.Request.Query["actor"].Count != 1 || ctx.Request.Query["actor"].ToString() != actorId) throw new UnauthorizedAccessException();
                    var edit = await ctx.Request.ReadFromJsonAsync<ManualNotificationPushActivationEdit>() ?? throw new ValidationException("入力が必要です。");
                    await ctx.Response.WriteAsJsonAsync(await service.SetManualPushActivationAsync(ctx.User, edit, ctx.RequestAborted));
                    return;
                }
                if (action == "manual-notification-history") {
                    if (post) { ctx.Response.StatusCode = 405; return; }
                    var actor = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(actor) || ctx.Request.Query["actor"].Count != 1 || ctx.Request.Query["actor"].ToString() != actor) throw new UnauthorizedAccessException();
                    if (ctx.Request.Query["id"].Count != 1) throw new ValidationException("イベントを確認してください。");
                    ManualNotificationHistoryCursor? before = null;
                    if (ctx.Request.Query.ContainsKey("beforeAt") || ctx.Request.Query.ContainsKey("beforeId")) {
                        var at = ctx.Request.Query["beforeAt"]; var request = ctx.Request.Query["beforeId"];
                        if (at.Count != 1 || request.Count != 1 || !long.TryParse(at.ToString(), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var seconds)) throw new ValidationException("履歴の続きを確認してください。");
                        before = new(seconds, request.ToString());
                    }
                    await ctx.Response.WriteAsJsonAsync(await service.ReadEventManualNotificationHistoryAsync(ctx.User, ctx.Request.Query["id"].ToString(), before, ctx.RequestAborted));
                    return;
                }
                if (action == "manual-notification-push-status") {
                    if (post) { ctx.Response.StatusCode = 405; return; }
                    var actor = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(actor) || ctx.Request.Query["actor"].Count != 1 || ctx.Request.Query["actor"].ToString() != actor) throw new UnauthorizedAccessException();
                    if (ctx.Request.Query["id"].Count != 1 || ctx.Request.Query["requestId"].Count != 1) throw new ValidationException("イベントと操作Idを確認してください。");
                    await ctx.Response.WriteAsJsonAsync(await service.ReadEventManualNotificationPushStatusAsync(ctx.User, ctx.Request.Query["id"].ToString(), ctx.Request.Query["requestId"].ToString(), ctx.RequestAborted));
                    return;
                }
                if (action == "manual-notification-context")
                {
                    if (post) { ctx.Response.StatusCode = 405; return; }
                    var view = await service.ReadEventTabsAsync(ctx.User, ctx.Request.Query["id"].ToString(), ctx.RequestAborted);
                    if (!view.Available.Contains("notifications")) throw new UnauthorizedAccessException();
                    await ctx.Response.WriteAsJsonAsync(new ManualNotificationComposerContext(view.UserId, view.EventId, view.Title));
                    return;
                }
                if (action is "manual-notification-preview" or "manual-notification-accept")
                {
                    if (!post) { ctx.Response.StatusCode = 405; return; }
                    // Bind this browser request to the actor that opened the composer. Not an auth token.
                    var actor = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(actor) || ctx.Request.Query["actor"].ToString() != actor)
                        throw new UnauthorizedAccessException();
                    var id = ctx.Request.Query["id"].ToString();
                    if (action == "manual-notification-preview") {
                        var draft = await ctx.Request.ReadFromJsonAsync<ManualNotificationDraft>() ?? throw new ValidationException("入力が必要です。");
                        await ctx.Response.WriteAsJsonAsync(await service.PreviewEventManualNotificationAsync(ctx.User, id, draft.Subject, draft.Body, ctx.RequestAborted));
                    } else {
                        var edit = await ctx.Request.ReadFromJsonAsync<ManualNotificationSubmit>() ?? throw new ValidationException("入力が必要です。");
                        await ctx.Response.WriteAsJsonAsync(await service.AcceptEventManualNotificationAsync(ctx.User, id, edit, ctx.RequestAborted));
                    }
                    return;
                }
                if (!post && action == "digest-preview") { await ctx.Response.WriteAsJsonAsync(await service.ReadDigestPreviewAsync(ctx.User, ctx.Request.Query.ContainsKey("day") ? ctx.Request.Query["day"].ToString() : null, ctx.RequestAborted)); return; }
                if (action == "participant-roster") { if (post) { ctx.Response.StatusCode = 405; return; } await ctx.Response.WriteAsJsonAsync(await service.ReadParticipantRosterAsync(ctx.User, ctx.Request.Query["id"].ToString(), ctx.Request.Query["afterUserId"].FirstOrDefault(), ctx.RequestAborted)); return; }
                if (action == "navigation") { if (post) { ctx.Response.StatusCode = 405; return; } await ctx.Response.WriteAsJsonAsync(await service.ReadNavigationAsync(ctx.User, ctx.RequestAborted)); return; }
                if (!post && action == "workspace") { await ctx.Response.WriteAsJsonAsync(await service.ReadAsync(ctx.User, ctx.RequestAborted)); return; }
                if (!post && action == "profile") { await ctx.Response.WriteAsJsonAsync(await service.ReadProfileAsync(ctx.User, ctx.Request.Query["id"].ToString(), ctx.RequestAborted)); return; }
                if (action == "participation") {
                    var id=ctx.Request.Query["id"].ToString();
                    if(post) await ctx.Response.WriteAsJsonAsync(await service.SubmitParticipationAsync(ctx.User,id,await ctx.Request.ReadFromJsonAsync<ParticipationEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));
                    else await ctx.Response.WriteAsJsonAsync(await service.ReadParticipationAsync(ctx.User,id,ctx.RequestAborted,ctx.Request.Query["subject"].FirstOrDefault()));return;
                }
                if(post && action == "capacity") { await service.SetCapacityAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<CapacityEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted);await ctx.Response.WriteAsJsonAsync(new{message="保存しました。"});return;}
                if(action=="event-push-rules"){
                    var id=ctx.Request.Query["id"].ToString();
                    if(post){await service.SaveEventPushRuleAsync(ctx.User,id,await ctx.Request.ReadFromJsonAsync<EventPushRuleEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted);await ctx.Response.WriteAsJsonAsync(new{message="保存しました。"});}
                    else await ctx.Response.WriteAsJsonAsync(await service.ReadEventPushRulesAsync(ctx.User,id,ctx.RequestAborted));return;
                }
                if(action=="part-start-preferences"){
                    var id=ctx.Request.Query["id"].ToString();
                    if(post){await service.SavePartStartPreferenceAsync(ctx.User,id,await ctx.Request.ReadFromJsonAsync<PartStartPreferenceEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted);await ctx.Response.WriteAsJsonAsync(new{message="保存しました。"});}
                    else await ctx.Response.WriteAsJsonAsync(await service.ReadPartStartPreferencesAsync(ctx.User,id,ctx.RequestAborted));return;
                }
                if(action=="comments"){
                    var id=ctx.Request.Query["id"].ToString();
                    if(post)await ctx.Response.WriteAsJsonAsync(await service.PostCommentAsync(ctx.User,id,await ctx.Request.ReadFromJsonAsync<CommentEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));
                    else {var at=ctx.Request.Query["beforeAt"].FirstOrDefault();var cursorId=ctx.Request.Query["beforeId"].FirstOrDefault();if((at is null)!=(cursorId is null))throw new ValidationException("一覧の続きを確認してください。");await ctx.Response.WriteAsJsonAsync(await service.ReadCommentsAsync(ctx.User,id,ctx.RequestAborted,at is null?null:new CommentCursor(at,cursorId!)));}return;
                }
                if(post&&action=="comment-delete"){await ctx.Response.WriteAsJsonAsync(await service.DeleteCommentAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<CommentDeleteEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));return;}
                if(!post&&action=="event-qr-image"){var id=ctx.Request.Query["id"].ToString();if(!long.TryParse(ctx.Request.Query["version"],out var version)||version<=0)throw new ValidationException("コードの版を確認してください。");var view=await service.ReadEventQrAsync(ctx.User,id,ctx.Request.Query["user"].FirstOrDefault(),ctx.RequestAborted);if(view.Token is null||view.TokenVersion!=version)throw new CoreConflictException();ctx.Response.ContentType="image/png";await ctx.Response.Body.WriteAsync(EventQrImage.Png(deployment.PublicUrl,id,view.Token),ctx.RequestAborted);return;}
                if(action=="event-qr"){var id=ctx.Request.Query["id"].ToString();if(post)await ctx.Response.WriteAsJsonAsync(await service.IssueEventQrAsync(ctx.User,id,await ctx.Request.ReadFromJsonAsync<EventQrEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));else await ctx.Response.WriteAsJsonAsync(await service.ReadEventQrAsync(ctx.User,id,ctx.Request.Query["user"].FirstOrDefault(),ctx.RequestAborted));return;}
                if(post&&action=="event-qr-read"){await ctx.Response.WriteAsJsonAsync(await service.RecordEventQrReadAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<EventQrReadEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));return;}
                if(!post&&action=="event-qr-history"){var at=ctx.Request.Query["beforeAt"].FirstOrDefault();var cursorId=ctx.Request.Query["beforeId"].FirstOrDefault();if((at is null)!=(cursorId is null))throw new ValidationException("履歴の続きを確認してください。");await ctx.Response.WriteAsJsonAsync(await service.ReadEventQrHistoryAsync(ctx.User,ctx.Request.Query["id"].ToString(),at is null?null:new CommentCursor(at,cursorId!),ctx.RequestAborted));return;}
                if(action=="reception-history"){if(post){ctx.Response.StatusCode=405;return;}var at=ctx.Request.Query["beforeAt"].FirstOrDefault();var cursorId=ctx.Request.Query["beforeId"].FirstOrDefault();if((at is null)!=(cursorId is null))throw new ValidationException("履歴の続きを確認してください。");await ctx.Response.WriteAsJsonAsync(await service.ReadReceptionHistoryAsync(ctx.User,ctx.Request.Query["id"].ToString(),ctx.Request.Query["user"].FirstOrDefault(),at is null?null:new CommentCursor(at,cursorId!),ctx.RequestAborted));return;}
                if(!post&&action=="reception-target"){await ctx.Response.WriteAsJsonAsync(await service.ReadReceptionTargetAsync(ctx.User,ctx.Request.Query["id"].ToString(),ctx.Request.Query["user"].ToString(),ctx.RequestAborted));return;}
                if(post&&action=="qr-reception"){await ctx.Response.WriteAsJsonAsync(await service.CompleteQrReceptionAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<QrReceptionEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));return;}
                if(post&&action=="manual-reception"){await ctx.Response.WriteAsJsonAsync(await service.CompleteManualReceptionAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<ManualReceptionEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));return;}
                if(post&&action=="reception-cancel"){await ctx.Response.WriteAsJsonAsync(await service.CancelReceptionAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<ReceptionCancelEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));return;}
                if(action=="reception-roster"){if(post){ctx.Response.StatusCode=405;return;}await ctx.Response.WriteAsJsonAsync(await service.ReadReceptionRosterAsync(ctx.User,ctx.Request.Query["id"].ToString(),ctx.Request.Query["q"].FirstOrDefault(),ctx.RequestAborted));return;}
                if(action=="public-answers"){if(post){ctx.Response.StatusCode=405;return;}await ctx.Response.WriteAsJsonAsync(await service.ReadPublicAnswersAsync(ctx.User,ctx.Request.Query["id"].ToString(),ctx.RequestAborted));return;}
                if(action=="notification-reservations"){if(post){ctx.Response.StatusCode=405;return;}await ctx.Response.WriteAsJsonAsync(await service.ReadNotificationReservationsAsync(ctx.User,ctx.Request.Query["id"].ToString(),ctx.RequestAborted));return;}
                if(action=="notification-plan"){if(post){ctx.Response.StatusCode=405;return;}await ctx.Response.WriteAsJsonAsync(await service.ReadNotificationPlanAsync(ctx.User,ctx.Request.Query["id"].ToString(),ctx.RequestAborted));return;}
                if(action=="progress") {if(post){ctx.Response.StatusCode=405;return;}await ctx.Response.WriteAsJsonAsync(await service.ReadProgressAsync(ctx.User,ctx.Request.Query["id"].ToString(),ctx.RequestAborted));return;}
                if(post&&action=="progress-record"){await ctx.Response.WriteAsJsonAsync(await service.RecordProgressAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<ProgressRecordEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));return;}
                if(post&&action=="progress-estimate"){await ctx.Response.WriteAsJsonAsync(await service.SaveProgressEstimateAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<ProgressEstimateEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));return;}
                if(action=="shortening"&&!post){await ctx.Response.WriteAsJsonAsync(await service.ReadShorteningAsync(ctx.User,ctx.Request.Query["id"].ToString(),ctx.RequestAborted));return;}
                if(action=="shortening-policy"&&post){await ctx.Response.WriteAsJsonAsync(await service.SaveShorteningPolicyAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<ShorteningPolicyEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));return;}
                if(action=="shortening-approve"&&post){await ctx.Response.WriteAsJsonAsync(await service.ApproveShorteningAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<ShorteningApproveEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));return;}
                if (action == "timetable") {
                    var id=ctx.Request.Query["id"].ToString();
                    if(post){var json=await ctx.Request.ReadFromJsonAsync<JsonElement>();if(json.ValueKind!=JsonValueKind.Object)throw new ValidationException("入力形式を確認してください。");foreach(var p in json.EnumerateObject().Where(x=>x.Name.Equals("plannedEndAt",StringComparison.OrdinalIgnoreCase)))if(p.Value.ValueKind!=JsonValueKind.Null&&(p.Value.ValueKind!=JsonValueKind.String||!Regex.IsMatch(p.Value.GetString()??"","(?:Z|[+-][0-9]{2}:[0-9]{2})$")))throw new ValidationException("終了予定はタイムゾーン付きで指定してください。");var edit=json.Deserialize<TimetableEdit>(new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new ValidationException("入力が必要です。");await ctx.Response.WriteAsJsonAsync(await service.SaveTimetableAsync(ctx.User,id,edit,ctx.RequestAborted));}
                    else await ctx.Response.WriteAsJsonAsync(await service.ReadTimetableAsync(ctx.User,id,ctx.RequestAborted));return;
                }
                if (action == "songs") {
                    var id=ctx.Request.Query["id"].ToString();
                    if(post) await ctx.Response.WriteAsJsonAsync(await service.SaveSongsAsync(ctx.User,id,await ctx.Request.ReadFromJsonAsync<SongPlanEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));
                    else await ctx.Response.WriteAsJsonAsync(await service.ReadSongsAsync(ctx.User,id,ctx.RequestAborted)); return;
                }
                if (action == "questions") {
                    var id=ctx.Request.Query["id"].ToString();
                    if(post) await ctx.Response.WriteAsJsonAsync(await service.SaveQuestionsAsync(ctx.User,id,await ctx.Request.ReadFromJsonAsync<EventQuestionPlanEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));
                    else await ctx.Response.WriteAsJsonAsync(await service.ReadQuestionsAsync(ctx.User,id,ctx.RequestAborted)); return;
                }
                if (action == "definitions") {
                    var id=ctx.Request.Query["id"].ToString(); var eventId=id.Length==0?null:id;
                    if(post) await ctx.Response.WriteAsJsonAsync(await service.SaveDefinitionsAsync(ctx.User,eventId,await ctx.Request.ReadFromJsonAsync<PartDefinitionPlanEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));
                    else await ctx.Response.WriteAsJsonAsync(await service.ReadDefinitionsAsync(ctx.User,eventId,ctx.RequestAborted)); return;
                }
                if (!post && action == "phases") { await ctx.Response.WriteAsJsonAsync(await service.ReadPhasePlanAsync(ctx.User, ctx.Request.Query["id"].ToString(), ctx.RequestAborted)); return; }
                if (post && action == "phases")
                {
                    var json = await ctx.Request.ReadFromJsonAsync<JsonElement>();
                    if (json.ValueKind != JsonValueKind.Object) throw new ValidationException("入力形式を確認してください。");
                    foreach (var prop in json.EnumerateObject().Where(p => p.Name.Equals("customPhases", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Array) throw new ValidationException("フェーズ配列を確認してください。");
                        foreach (var phase in prop.Value.EnumerateArray())
                        {
                            if (phase.ValueKind != JsonValueKind.Object) throw new ValidationException("フェーズ入力を確認してください。");
                            foreach (var date in phase.EnumerateObject().Where(p => p.Name.Equals("activateAt", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("deactivateAt", StringComparison.OrdinalIgnoreCase)))
                                if (date.Value.ValueKind != JsonValueKind.Null && (date.Value.ValueKind != JsonValueKind.String || !Regex.IsMatch(date.Value.GetString() ?? "", "(?:Z|[+-][0-9]{2}:[0-9]{2})$")))
                                    throw new ValidationException("フェーズ日時はタイムゾーンを含むISO-8601で指定してください。");
                        }
                    }
                    var edit = json.Deserialize<PhasePlanEdit>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ValidationException("入力が必要です。");
                    await ctx.Response.WriteAsJsonAsync(await service.SavePhasePlanAsync(ctx.User, ctx.Request.Query["id"].ToString(), edit, ctx.RequestAborted)); return;
                }
                if (post && action == "service")
                    await service.SaveServiceAsync(ctx.User, await ctx.Request.ReadFromJsonAsync<ServiceEdit>() ?? throw new ValidationException("入力が必要です。"), ctx.RequestAborted);
                else if (post && action == "profile")
                    await service.SaveProfileAsync(ctx.User, await ctx.Request.ReadFromJsonAsync<ProfileEdit>() ?? throw new ValidationException("入力が必要です。"), ctx.RequestAborted);
                else if (post && action == "event")
                {
                    var json = await ctx.Request.ReadFromJsonAsync<JsonElement>();
                    if (json.ValueKind != JsonValueKind.Object) throw new ValidationException("入力形式を確認してください。");
                    foreach (var property in json.EnumerateObject())
                        if ((property.Name.Equals("scheduledDate", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("opensAt", StringComparison.OrdinalIgnoreCase)) &&
                            property.Value.ValueKind != JsonValueKind.Null && (property.Value.ValueKind != JsonValueKind.String ||
                            !Regex.IsMatch(property.Value.GetString() ?? "", "(?:Z|[+-][0-9]{2}:[0-9]{2})$")))
                            throw new ValidationException("日時はタイムゾーンを含むISO-8601で指定してください。");
                    var edit = json.Deserialize<EventEdit>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ValidationException("入力が必要です。");
                    var id = ctx.Request.Query["id"].ToString();
                    var saved = await service.SaveEventAsync(ctx.User, id.Length == 0 ? null : id, edit, ctx.RequestAborted);
                    await ctx.Response.WriteAsJsonAsync(new { id = saved }); return;
                }
                else { ctx.Response.StatusCode = 404; return; }
                await ctx.Response.WriteAsJsonAsync(new { message = "保存しました。" });
            }
            catch (EventImportPayloadTooLargeException) { ctx.Response.StatusCode = 413; await ctx.Response.WriteAsJsonAsync(new { error = "取込リクエストが大きすぎます。" }); }
            catch (CoreRequestPayloadTooLargeException) { ctx.Response.StatusCode = 413; await ctx.Response.WriteAsJsonAsync(new { error = "リクエストが大きすぎます。" }); }
            catch (UnauthorizedAccessException) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "ログイン状態と操作権限を確認してください。" }); }
            catch (CoreConflictException e) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (DbUpdateConcurrencyException) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "更新が競合しました。最新状態を確認してください。" }); }
            catch (DbUpdateException e) when (e.InnerException is SqliteException sql && sql.SqliteErrorCode is 5 or 6 or 19) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "更新が競合しました。最新状態を確認してください。" }); }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "ほかの処理が更新中です。最新状態を確認してください。" }); }
            catch (ValidationException e) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "ページを再読み込みしてください。" }); }
            catch (JsonException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "入力形式を確認してください。" }); }
        });

    private const int ThemeAppearanceRequestMaxBytes = 65_536;
    private static readonly JsonSerializerOptions ThemeAppearanceJsonOptions = new(JsonSerializerDefaults.Web)
    { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };

    private static string RequiredThemeQueryValue(IQueryCollection query, string name)
    {
        var values = query[name];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]) || values[0]!.Length > 128 || values[0]!.Any(char.IsControl))
            throw new ValidationException(name == "expectedActor" ? "操作者を確認してください。" : "識別子を確認してください。");
        return values[0]!;
    }

    private static string? OptionalThemeQueryValue(IQueryCollection query, string name)
    {
        if (!query.ContainsKey(name)) return null;
        return RequiredThemeQueryValue(query, name);
    }

    private static async Task<T> ReadThemeAppearanceRequestAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType()) throw new ValidationException("JSON形式で送信してください。");
        if (request.ContentLength > ThemeAppearanceRequestMaxBytes) throw new CoreRequestPayloadTooLargeException();
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = ThemeAppearanceRequestMaxBytes + 1 - (int)body.Length;
            if (remaining <= 0) throw new CoreRequestPayloadTooLargeException();
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) break;
            await body.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (body.Length > ThemeAppearanceRequestMaxBytes) throw new CoreRequestPayloadTooLargeException();
        return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), ThemeAppearanceJsonOptions)
            ?? throw new ValidationException("入力が必要です。");
    }

    private sealed class CoreRequestPayloadTooLargeException : Exception { }
}
