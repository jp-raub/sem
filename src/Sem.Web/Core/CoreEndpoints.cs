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
    public static void MapCoreFoundation(this WebApplication app) => app.MapMethods("/api/core/{action}", ["GET", "POST"],
        async (HttpContext ctx, CoreFoundationService service, IAntiforgery csrf, DeploymentSettings deployment) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            try
            {
                var action = ctx.Request.RouteValues["action"]?.ToString();
                var post = HttpMethods.IsPost(ctx.Request.Method);
                if (post)
                {
                    if (ctx.Request.Headers.Origin.ToString() != deployment.PublicUrl.TrimEnd('/')) throw new UnauthorizedAccessException();
                    var size = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
                    if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 65536;
                    await csrf.ValidateRequestAsync(ctx);
                }
                if (!post && action == "workspace") { await ctx.Response.WriteAsJsonAsync(await service.ReadAsync(ctx.User, ctx.RequestAborted)); return; }
                if (!post && action == "profile") { await ctx.Response.WriteAsJsonAsync(await service.ReadProfileAsync(ctx.User, ctx.Request.Query["id"].ToString(), ctx.RequestAborted)); return; }
                if (action == "participation") {
                    var id=ctx.Request.Query["id"].ToString();
                    if(post) await ctx.Response.WriteAsJsonAsync(await service.SubmitParticipationAsync(ctx.User,id,await ctx.Request.ReadFromJsonAsync<ParticipationEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted));
                    else await ctx.Response.WriteAsJsonAsync(await service.ReadParticipationAsync(ctx.User,id,ctx.RequestAborted,ctx.Request.Query["subject"].FirstOrDefault()));return;
                }
                if(post && action == "capacity") { await service.SetCapacityAsync(ctx.User,ctx.Request.Query["id"].ToString(),await ctx.Request.ReadFromJsonAsync<CapacityEdit>()??throw new ValidationException("入力が必要です。"),ctx.RequestAborted);await ctx.Response.WriteAsJsonAsync(new{message="保存しました。"});return;}
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
            catch (UnauthorizedAccessException) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "ログイン状態と操作権限を確認してください。" }); }
            catch (CoreConflictException e) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (DbUpdateConcurrencyException) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "更新が競合しました。最新状態を確認してください。" }); }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "ほかの処理が更新中です。最新状態を確認してください。" }); }
            catch (ValidationException e) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "ページを再読み込みしてください。" }); }
            catch (JsonException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "入力形式を確認してください。" }); }
        });
}
